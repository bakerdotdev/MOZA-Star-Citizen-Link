param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\MozaStarCitizen.App\MozaStarCitizen.App.csproj"
$publishRoot = Join-Path $root "artifacts\publish"
$stamp = Get-Date -Format "yyyyMMddHHmmss"
$publishDir = Join-Path $publishRoot "MozaStarCitizen-$Runtime-$stamp"
$zipPath = Join-Path $root "artifacts\MozaStarCitizen-$Runtime-portable.zip"

New-Item -ItemType Directory -Path $publishDir | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path (Join-Path $publishDir "MozaStarCitizen.exe"))) {
    throw "Publish completed, but MozaStarCitizen.exe was not produced."
}

Get-ChildItem $publishDir -Filter "*.pdb" -File | Remove-Item -Force

function Write-Launcher {
    param(
        [string]$Name,
        [string]$Mode,
        [string[]]$ExtraEnvironment = @()
    )

    $lines = @(
        "@echo off",
        "set MOZA_SC_OUTPUT=$Mode"
    )
    foreach ($entry in $ExtraEnvironment) {
        $lines += "set $entry"
    }
    $lines += @(
        "start """" ""%~dp0MozaStarCitizen.exe"""
    )
    Set-Content -Path (Join-Path $publishDir $Name) -Value $lines -Encoding ASCII
}

Write-Launcher "Run-Auto.cmd" ""
Write-Launcher "Run-Audio.cmd" "DirectInput" @("MOZA_SC_TELEMETRY=AudioDsp")
Write-Launcher "Run-DirectInput.cmd" "DirectInput"
Write-Launcher "Run-Preview.cmd" "Preview"
Write-Launcher "Run-DBoxTelemetry.cmd" "DirectInput" @("MOZA_SC_TELEMETRY=DBoxHaptiSync")

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force
Write-Host "Portable build written to $zipPath"
