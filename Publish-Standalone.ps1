#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'publish-standalone')
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'WingetX.csproj'
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)

& dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "Standalone publish failed with exit code $LASTEXITCODE."
}

$executable = Get-Item -LiteralPath (Join-Path $outputPath 'wingetx.exe')
$looseDependencies = @(Get-ChildItem -LiteralPath $outputPath -File |
    Where-Object { $_.Extension -eq '.dll' -or $_.Name -match '\.(runtimeconfig|deps)\.json$' })
if ($looseDependencies.Count -gt 0) {
    throw 'The publish directory contains loose dependencies. Do not deploy this build as a single executable.'
}

[pscustomobject]@{
    Executable = $executable.FullName
    Version = $executable.VersionInfo.FileVersion
    SizeBytes = $executable.Length
    SHA256 = (Get-FileHash -LiteralPath $executable.FullName -Algorithm SHA256).Hash
}
