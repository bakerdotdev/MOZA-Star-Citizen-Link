param(
    [string]$TargetDir = "C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $TargetDir)) {
    throw "Target directory not found: $TargetDir"
}

$targetDll = Join-Path $TargetDir "dbxLive64.dll"
$realDll   = Join-Path $TargetDir "dbxLive64_real.dll"

if (-not (Test-Path -LiteralPath $realDll)) {
    Write-Host "No proxy install detected (dbxLive64_real.dll not found in $TargetDir)." -ForegroundColor Yellow
    exit 0
}

if (Test-Path -LiteralPath $targetDll) {
    Remove-Item -LiteralPath $targetDll -Force
}
Rename-Item -LiteralPath $realDll -NewName "dbxLive64.dll"

Write-Host "Restored original dbxLive64.dll in $TargetDir" -ForegroundColor Green
