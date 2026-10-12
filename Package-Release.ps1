#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $RuntimePackageDirectory,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'WingetX.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$executable = Join-Path $PSScriptRoot 'publish-standalone\wingetx.exe'
if ((Get-Item -LiteralPath $executable).VersionInfo.FileVersion -ne "$version.0") {
    throw 'Build version does not match the project. Run Publish-Standalone.ps1 first.'
}
if ([string]::IsNullOrWhiteSpace($RuntimePackageDirectory)) {
    $assets = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\win-x64\wingetx.runtimeconfig.json') -Raw | ConvertFrom-Json
    $framework = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -EQ 'Microsoft.NETCore.App')
    if ($framework.Count -ne 1) { throw 'Cannot determine the self-contained runtime version. Publish first.' }
    $runtimeRelativePath = 'microsoft.netcore.app.runtime.win-x64/' + $framework[0].version
    foreach ($packageFolder in $assets.packageFolders.Keys) {
        $candidate = Join-Path $packageFolder $runtimeRelativePath
        if (Test-Path -LiteralPath $candidate) { $RuntimePackageDirectory = $candidate; break }
    }
}
if ([string]::IsNullOrWhiteSpace($RuntimePackageDirectory)) { throw 'Runtime package directory was not found.' }

$output = [IO.Path]::GetFullPath($OutputDirectory)
$archive = Join-Path $output "wingetx-$version-win-x64.zip"
if (Test-Path -LiteralPath $archive) { throw "Archive already exists: $archive" }
$stage = Join-Path $output ('.stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $stage 'wingetx.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $RuntimePackageDirectory 'LICENSE.TXT') -Destination (Join-Path $stage 'DOTNET-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $RuntimePackageDirectory 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $stage 'DOTNET-THIRD-PARTY-NOTICES.txt')
Compress-Archive -LiteralPath @(Get-ChildItem -LiteralPath $stage -File | Select-Object -ExpandProperty FullName) -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Get-Item -LiteralPath $archive, (Join-Path $output 'SHA256SUMS.txt')
