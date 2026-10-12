#requires -Version 7.0
<#
.SYNOPSIS
Tests a published wingetx against a fake winget, without installing real software.
.DESCRIPTION
Every tested process receives its own tracking database, fake state/log, temporary
directory and bundle extraction directory. No environment variables are changed in
the calling PowerShell session. Test artifacts are kept beneath tests/artifacts.
The fake project is rebuilt unless an explicit FakeWingetPath is supplied.
#>
[CmdletBinding()]
param(
    [string] $ExePath = (Join-Path $PSScriptRoot '..\publish-standalone\wingetx.exe'),
    [string] $FakeWingetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -or [IntPtr]::Size -ne 8) {
    throw 'Run this test with 64-bit PowerShell 7 on Windows.'
}

$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if ([string]::IsNullOrWhiteSpace($FakeWingetPath)) {
    $fakeProject = Join-Path $PSScriptRoot 'FakeWinget\FakeWinget.csproj'
    & dotnet build $fakeProject --configuration Release --runtime win-x64 --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Building FakeWinget failed with exit code $LASTEXITCODE."
    }
    $FakeWingetPath = Join-Path $PSScriptRoot 'FakeWinget\bin\Release\net8.0-windows\win-x64\fake-winget.exe'
}
$FakeWingetPath = (Resolve-Path -LiteralPath $FakeWingetPath).Path
if ([IO.Path]::GetFileName($FakeWingetPath) -ine 'fake-winget.exe') {
    throw 'FakeWingetPath must name fake-winget.exe; never point this test at real winget.'
}
if ($ExePath -ieq $FakeWingetPath) {
    throw 'The wrapper and fake executables must be different files.'
}

$runName = 'standalone-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$testRoot = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot "artifacts\$runName")).FullName
$bundleRoot = (New-Item -ItemType Directory -Path (Join-Path $testRoot 'private-bundle')).FullName
$script:assertionCount = 0
$codes = @{
    InvalidArguments = [int] 0x8A150002
    CommandFailed = [int] 0x8A150003
    NoApplicationsFound = [int] 0x8A150014
    NoManifestFound = [int] 0x8A150017
    UpdateNotApplicable = [int] 0x8A15002B
    UpdateAllHasFailure = [int] 0x8A15002C
    UpgradeVersionNotNewer = [int] 0x8A15004F
    MultipleInstallFailed = [int] 0x8A150065
}
$autoFlags = @('--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity')

function Assert-True {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw "ASSERTION FAILED: $Message"
    }
    $script:assertionCount++
}

function New-TestCase {
    param([string] $Name)
    $directory = (New-Item -ItemType Directory -Path (Join-Path $testRoot $Name)).FullName
    $temporary = (New-Item -ItemType Directory -Path (Join-Path $directory 'temp')).FullName
    return [pscustomobject] @{
        Name = $Name
        Directory = $directory
        DataDirectory = (Join-Path $directory 'data')
        Database = (Join-Path $directory 'data\packages.json')
        State = (Join-Path $directory 'fake-state.json')
        Log = (Join-Path $directory 'fake-calls.log')
        Temporary = $temporary
    }
}

function Start-TestProcess {
    param($Case, [string[]] $Arguments, [hashtable] $ExtraEnvironment = @{})
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    $startInfo.WorkingDirectory = $Case.Directory
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    # Remove inherited test overrides, then inject them only into this child.
    foreach ($key in @($startInfo.Environment.Keys)) {
        if ($key -like 'WINGETX_*' -or $key -like 'FAKE_WINGET_*') {
            [void] $startInfo.Environment.Remove($key)
        }
    }
    $startInfo.Environment['WINGETX_WINGET_PATH'] = $FakeWingetPath
    $startInfo.Environment['WINGETX_DATA_DIR'] = $Case.DataDirectory
    $startInfo.Environment['FAKE_WINGET_STATE'] = $Case.State
    $startInfo.Environment['FAKE_WINGET_LOG'] = $Case.Log
    $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $bundleRoot
    $startInfo.Environment['TEMP'] = $Case.Temporary
    $startInfo.Environment['TMP'] = $Case.Temporary
    foreach ($key in $ExtraEnvironment.Keys) {
        $startInfo.Environment[$key] = [string] $ExtraEnvironment[$key]
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void] $process.Start()
    return [pscustomobject] @{
        Process = $process
        Output = $process.StandardOutput.ReadToEndAsync()
        Error = $process.StandardError.ReadToEndAsync()
        Arguments = $Arguments
    }
}

function Complete-TestProcess {
    param($Running, [int] $TimeoutMilliseconds = 30000)
    try {
        if (-not $Running.Process.WaitForExit($TimeoutMilliseconds)) {
            # This is an isolated test process whose only native child is fake-winget.
            $Running.Process.Kill($true)
            [void] $Running.Process.WaitForExit(5000)
            throw "Test process timed out: $($Running.Arguments -join ' ')"
        }
        return [pscustomobject] @{
            ExitCode = $Running.Process.ExitCode
            Output = $Running.Output.GetAwaiter().GetResult()
            Error = $Running.Error.GetAwaiter().GetResult()
        }
    }
    finally {
        $Running.Process.Dispose()
    }
}

function Invoke-TestCommand {
    param($Case, [string[]] $Arguments, [int] $ExpectedExitCode = 0)
    $result = Complete-TestProcess (Start-TestProcess $Case $Arguments)
    Assert-True ($result.ExitCode -eq $ExpectedExitCode) (
        "'$($Arguments -join ' ')' returned $($result.ExitCode), expected $ExpectedExitCode.`n" +
        $result.Output + $result.Error)
    return $result
}

function Get-FakeCalls {
    param($Case)
    if (Test-Path -LiteralPath $Case.Log) {
        Get-Content -LiteralPath $Case.Log -Encoding utf8
    }
}

function Assert-LastCall {
    param($Case, [string[]] $ExpectedArguments)
    # Always preserve an array: one log line must not become a character index.
    $calls = @(Get-FakeCalls $Case)
    Assert-True ($calls.Count -gt 0) "No fake call was recorded for $($Case.Name)."
    Assert-True ($calls[-1] -ceq ($ExpectedArguments -join "`t")) (
        "Argument forwarding differs.`nExpected: $($ExpectedArguments -join ' | ')`nActual: $($calls[-1])")
}

function Get-TrackedPackages {
    param($Case)
    if (Test-Path -LiteralPath $Case.Database) {
        (Get-Content -LiteralPath $Case.Database -Raw -Encoding utf8 | ConvertFrom-Json).packages
    }
}

Write-Host "Test artifacts: $testRoot"
Write-Host 'Checking startup without a shared .NET runtime...'
$standaloneCase = New-TestCase 'no-shared-runtime'
$emptyDotnetRoot = (New-Item -ItemType Directory -Path (Join-Path $standaloneCase.Directory 'empty-dotnet-root')).FullName
$hostTracePath = Join-Path $standaloneCase.Directory 'corehost-trace.txt'
$standaloneResult = Complete-TestProcess (Start-TestProcess $standaloneCase @('upall', '--help') @{
    DOTNET_ROOT = $emptyDotnetRoot
    DOTNET_ROOT_X64 = $emptyDotnetRoot
    DOTNET_MULTILEVEL_LOOKUP = '0'
    COREHOST_TRACE = '1'
    COREHOST_TRACEFILE = $hostTracePath
    COREHOST_TRACE_VERBOSITY = '4'
})
Assert-True ($standaloneResult.ExitCode -eq 0) (
    "The wrapper could not start with shared runtime resolution disabled: $($standaloneResult.Error)")
Assert-True (@(Get-ChildItem -LiteralPath $emptyDotnetRoot -Force).Count -eq 0) 'The supposedly empty runtime directory is not empty.'
Assert-True (@(Get-FakeCalls $standaloneCase).Count -eq 0) 'The standalone help probe unexpectedly invoked fake-winget.'
Assert-True (-not (Test-Path -LiteralPath $standaloneCase.Database)) 'The standalone help probe created a database.'
Assert-True (Test-Path -LiteralPath $hostTracePath) 'The standalone help probe did not produce a host trace.'
$hostTrace = Get-Content -LiteralPath $hostTracePath -Raw -Encoding utf8
$selfContainedConfirmed = $hostTrace.Contains('Executing as a self-contained app')
Assert-True $selfContainedConfirmed 'The host trace did not confirm self-contained execution.'

Write-Host 'Checking private runtime module loading...'
$probeCase = New-TestCase 'runtime-probe'
$releaseFile = Join-Path $probeCase.Directory 'release-fake'
$probe = Start-TestProcess $probeCase @('upgrade', '--id', 'Probe.Package', '--exact') @{
    FAKE_WINGET_WAIT_MS = '15000'
    FAKE_WINGET_RELEASE_FILE = $releaseFile
}
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (@(Get-FakeCalls $probeCase).Count -eq 0 -and -not $probe.Process.HasExited -and $watch.ElapsedMilliseconds -lt 10000) {
        Start-Sleep -Milliseconds 50
    }
    Assert-True (-not $probe.Process.HasExited) 'The wrapper exited before runtime modules could be inspected.'
    Assert-True (@(Get-FakeCalls $probeCase).Count -eq 1) 'The module probe did not reach fake-winget.'
    $probe.Process.Refresh()
    $modules = @($probe.Process.Modules | Select-Object ModuleName, FileName)
    [IO.File]::WriteAllLines((Join-Path $probeCase.Directory 'module-paths.txt'), [string[]] @(
        $modules | ForEach-Object { "$($_.ModuleName)`t$($_.FileName)" }
    ))
    $bundlePrefix = $bundleRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($moduleName in @('coreclr.dll', 'clrjit.dll')) {
        $matching = @($modules | Where-Object ModuleName -EQ $moduleName)
        Assert-True ($matching.Count -le 1) "Unexpected duplicate $moduleName modules."
        if ($matching.Count -eq 1) {
            Assert-True ($matching[0].FileName.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) (
                "$moduleName was not loaded from the private bundle directory: $($matching[0].FileName)")
            Write-Host "  $moduleName -> $($matching[0].FileName)"
        }
        else {
            # Windows singlefilehost may statically link CoreCLR and ClrJIT into
            # the apphost. Absence of separate DLL modules is valid only after
            # the trace proved self-contained startup and fake-winget was reached.
            Assert-True $selfContainedConfirmed "Cannot explain the absent $moduleName without standalone host evidence."
            Write-Host "  $moduleName -> no separate DLL (self-contained host confirmed)"
        }
    }
    # Managed assemblies normally remain memory-loaded in a single-file bundle.
    # If CoreLib is exposed as a physical module, it must still be private.
    foreach ($coreLibrary in @($modules | Where-Object ModuleName -EQ 'System.Private.CoreLib.dll')) {
        Assert-True ($coreLibrary.FileName.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) (
            "CoreLib is not private: $($coreLibrary.FileName)")
    }
    $sharedDotnetModules = @($modules | Where-Object FileName -Match '(?i)\\Program Files(?: \(x86\))?\\dotnet\\')
    Assert-True ($sharedDotnetModules.Count -eq 0) 'The wrapper loaded a module from the shared Program Files\dotnet installation.'
}
finally {
    [IO.File]::WriteAllText($releaseFile, 'release')
    $probeResult = Complete-TestProcess $probe
}
Assert-True ($probeResult.ExitCode -eq 0) "The private runtime probe failed: $($probeResult.Error)"

Write-Host 'Checking passthrough arguments and native exit codes...'
$passthrough = New-TestCase 'passthrough'
$rawArguments = @('upgrade', '--id', 'Vendor.Package', '--exact', '--log', (Join-Path $passthrough.Directory 'path with spaces.log'), '--override', 'NAME="two words"', '--silent')
$null = Invoke-TestCommand $passthrough $rawArguments
Assert-LastCall $passthrough $rawArguments
Assert-True (-not (Test-Path -LiteralPath $passthrough.Database)) 'Native upgrade unexpectedly created a tracking database.'
foreach ($example in @(
    @('Fail.Package', $codes.CommandFailed),
    @('Current.Package', $codes.UpdateNotApplicable),
    @('Newer.Package', $codes.UpgradeVersionNotNewer),
    @('Missing.Package', $codes.NoApplicationsFound),
    @('NoManifest.Package', $codes.NoManifestFound)
)) {
    $null = Invoke-TestCommand $passthrough @('upgrade', '--id', $example[0]) $example[1]
    Assert-LastCall $passthrough @('upgrade', '--id', $example[0])
}

Write-Host 'Checking --tracked versus upall flag semantics...'
$managed = New-TestCase 'managed-flags'
$null = Invoke-TestCommand $managed @('track', 'Test.Package', '--source', 'winget', '--architecture', 'x64', '--scope', 'user')
$selectors = @('upgrade', '--id', 'Test.Package', '--exact', '--source', 'winget', '--architecture', 'x64', '--scope', 'user')
$extraFlags = @('--silent', '--log', (Join-Path $managed.Directory 'upgrade log.txt'))
$null = Invoke-TestCommand $managed (@('upgrade', '--tracked') + $extraFlags)
Assert-LastCall $managed ($selectors + $extraFlags)
$null = Invoke-TestCommand $managed (@('update', '--tracked') + $extraFlags)
Assert-LastCall $managed ($selectors + $extraFlags)
$null = Invoke-TestCommand $managed (@('upall') + $extraFlags)
Assert-LastCall $managed ($selectors + $extraFlags + $autoFlags)
$null = Invoke-TestCommand $managed @('upall', '--accept-source-agreements', '--silent')
Assert-LastCall $managed ($selectors + @('--accept-source-agreements', '--silent', '--accept-package-agreements', '--disable-interactivity'))

$callCountBefore = @(Get-FakeCalls $managed).Count
$hashBefore = (Get-FileHash -LiteralPath $managed.Database).Hash
foreach ($arguments in @(
    @('upall', '--all'),
    @('upgrade', '--tracked', '--all')
)) {
    $null = Invoke-TestCommand $managed $arguments $codes.InvalidArguments
}
foreach ($arguments in @(
    @('upall', '--help'),
    @('upgrade', '--tracked', '--help')
)) {
    $null = Invoke-TestCommand $managed $arguments
}
Assert-True (@(Get-FakeCalls $managed).Count -eq $callCountBefore) 'Conflict/help handling unexpectedly invoked winget.'
Assert-True ((Get-FileHash -LiteralPath $managed.Database).Hash -eq $hashBefore) 'Conflict/help handling changed the tracking database.'

Write-Host 'Checking aggregate success/current/unavailable/failure handling...'
$aggregate = New-TestCase 'aggregate-codes'
$trackedIds = @('Success.Package', 'Current.Package', 'Newer.Package', 'Missing.Package', 'NoManifest.Package', 'Fail.Package')
$null = Invoke-TestCommand $aggregate (@('track') + $trackedIds + @('--source', 'winget', '--architecture', 'x64', '--scope', 'user'))
$aggregateResult = Invoke-TestCommand $aggregate @('upall') $codes.UpdateAllHasFailure
$aggregateCalls = @(Get-FakeCalls $aggregate)
Assert-True ($aggregateCalls.Count -eq 6) 'Not every tracked package was attempted after a failure.'
Assert-True ($aggregateResult.Output.Contains('成功 1，已是最新 2，来源不可用 2，失败 1')) 'The aggregate counters are incorrect.'
foreach ($call in $aggregateCalls) {
    $tokens = @($call -split "`t")
    foreach ($flag in $autoFlags) {
        Assert-True (@($tokens | Where-Object { $_ -ceq $flag }).Count -eq 1) "Automatic flag $flag was absent or duplicated."
    }
}
$null = Invoke-TestCommand $aggregate @('untrack', 'Fail.Package')
$currentResult = Invoke-TestCommand $aggregate @('upgrade', '--tracked')
Assert-True ($currentResult.Output.Contains('成功 1，已是最新 2，来源不可用 2，失败 0')) 'Current/unavailable packages did not aggregate to success.'

Write-Host 'Checking automatic install tracking and uninstall removal...'
$installCase = New-TestCase 'install-uninstall'
$null = Invoke-TestCommand $installCase @('install', '--id', 'Test.Install', '--exact', '--source', 'winget', '--architecture', 'x64', '--scope', 'user', '--silent')
$installedPackages = @(Get-TrackedPackages $installCase)
Assert-True ($installedPackages.Count -eq 1) 'Explicit install was not tracked exactly once.'
Assert-True ($installedPackages[0].packageIdentifier -ceq 'Test.Install') 'The installed ID was not preserved.'
Assert-True ($installedPackages[0].source -ceq 'winget' -and $installedPackages[0].architecture -ceq 'x64' -and $installedPackages[0].scope -ceq 'user') 'Install source/architecture/scope were not preserved.'
$null = Invoke-TestCommand $installCase @('uninstall', '--id', 'Test.Install', '--exact', '--silent')
Assert-True (@(Get-TrackedPackages $installCase).Count -eq 0) 'Successful uninstall did not remove its tracked entry.'
$null = Invoke-TestCommand $installCase @('install', 'FriendlyName', '--silent')
$resolvedPackages = @(Get-TrackedPackages $installCase)
Assert-True ($resolvedPackages.Count -eq 1 -and $resolvedPackages[0].packageIdentifier -ceq 'Vendor.Friendly') 'A friendly-name install was not tracked by resolved ID.'
$null = Invoke-TestCommand $installCase @('install', 'Partial.Fail', '--silent') $codes.MultipleInstallFailed
$partialPackages = @(Get-TrackedPackages $installCase)
Assert-True (@($partialPackages | Where-Object packageIdentifier -CEQ 'Good.Package').Count -eq 1) 'The successful part of a partial install was not tracked.'
Assert-True (@($partialPackages | Where-Object packageIdentifier -CEQ 'Partial.Fail').Count -eq 0) 'The failed install was incorrectly tracked.'

Write-Host "PASS: $script:assertionCount assertions. No real winget command or installer was run."
Write-Host "Artifacts retained at: $testRoot"
