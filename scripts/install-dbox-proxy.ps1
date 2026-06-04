param(
    [string]$ProxyDll  = "",
    [string]$TargetDir = "C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProxyDll)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $ProxyDll  = Join-Path $repoRoot "tools\dbox-proxy\out\dbxLive64.dll"
}

if (-not (Test-Path -LiteralPath $ProxyDll)) {
    throw "Proxy DLL not found: $ProxyDll. Run tools\dbox-proxy\build.cmd first."
}

if (-not (Test-Path -LiteralPath $TargetDir)) {
    throw "Target directory not found: $TargetDir"
}

$targetDll = Join-Path $TargetDir "dbxLive64.dll"
$realDll   = Join-Path $TargetDir "dbxLive64_real.dll"
$backupDir = Join-Path $TargetDir "_backup-moza-proxy"

if (-not (Test-Path -LiteralPath $targetDll)) {
    throw "Target dbxLive64.dll not found: $targetDll"
}

$targetVersion = (Get-Item -LiteralPath $targetDll).VersionInfo.FileVersion
$proxyVersion  = (Get-Item -LiteralPath $ProxyDll).VersionInfo.FileVersion

if ((Test-Path -LiteralPath $realDll) -and -not $Force) {
    Write-Host "Proxy is already installed (dbxLive64_real.dll exists in $TargetDir)." -ForegroundColor Yellow
    Write-Host "Use -Force to reinstall, or run uninstall-dbox-proxy.ps1 first." -ForegroundColor Yellow
    exit 1
}

New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupFile = Join-Path $backupDir "dbxLive64-$stamp.dll"
Copy-Item -LiteralPath $targetDll -Destination $backupFile -Force
Write-Host "Backup written: $backupFile" -ForegroundColor Green

if (Test-Path -LiteralPath $realDll) {
    Remove-Item -LiteralPath $realDll -Force
}
Rename-Item -LiteralPath $targetDll -NewName "dbxLive64_real.dll"
Copy-Item -LiteralPath $ProxyDll -Destination $targetDll -Force

Write-Host "Installed proxy:" -ForegroundColor Green
Write-Host "  $targetDll  (proxy, original FileVersion=$targetVersion, proxy FileVersion=$proxyVersion)"
Write-Host "  $realDll  (renamed original)"
Write-Host ""
Write-Host "Logs will be written to: $env:LOCALAPPDATA\MozaStarCitizen\dbx-trace\dbx-trace-*.log" -ForegroundColor Cyan
Write-Host "Run scripts\uninstall-dbox-proxy.ps1 to revert." -ForegroundColor Cyan
