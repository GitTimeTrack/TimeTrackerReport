[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipTests) {
        dotnet test desktop/TimeTracker.Core.Tests/TimeTracker.Core.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    }
    $output = Join-Path $root "artifacts/TimeTrackerReport-$Runtime"
    $versionOptions = @()
    if ($PSBoundParameters.ContainsKey('Version')) { $versionOptions += "-p:Version=$Version" }
    dotnet publish desktop/TimeTracker.Windows/TimeTracker.Windows.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $output @versionOptions
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    Copy-Item (Join-Path $root 'README.md') $output
    $archive = "$output.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    Write-Host "Windows package: $archive"
} finally { Pop-Location }
