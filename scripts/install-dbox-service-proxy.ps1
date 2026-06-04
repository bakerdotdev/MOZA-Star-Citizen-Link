param(
    [string]$ProxyDll  = "",
    [string]$TargetDir = "C:\ProgramData\D-BOX\MotionService",
    [switch]$Force,
    [switch]$SkipServiceRestart
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProxyDll)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $ProxyDll  = Join-Path $repoRoot "tools\dbox-service-proxy\out\dbxService64.dll"
}

if (-not (Test-Path -LiteralPath $ProxyDll)) {
    throw "Proxy DLL not found: $ProxyDll. Run tools\dbox-service-proxy\build.cmd first."
}

if (-not (Test-Path -LiteralPath $TargetDir)) {
    throw "Target directory not found: $TargetDir"
}

$targetDll = Join-Path $TargetDir "dbxService64.dll"
$realDll   = Join-Path $TargetDir "dbxService64_real.dll"
$backupDir = Join-Path $TargetDir "_backup-moza-proxy"

if (-not (Test-Path -LiteralPath $targetDll)) {
    throw "Target dbxService64.dll not found: $targetDll"
}

$targetVersion = (Get-Item -LiteralPath $targetDll).VersionInfo.FileVersion
$proxyVersion  = (Get-Item -LiteralPath $ProxyDll).VersionInfo.FileVersion

if ((Test-Path -LiteralPath $realDll) -and -not $Force) {
    Write-Host "Proxy is already installed (dbxService64_real.dll exists in $TargetDir)." -ForegroundColor Yellow
    Write-Host "Use -Force to reinstall, or run uninstall-dbox-service-proxy.ps1 first." -ForegroundColor Yellow
    exit 1
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell. C:\ProgramData\D-BOX\MotionService is write-protected."
}

$services = @("DboxMotionEngine", "DboxHaptiSyncApi", "DboxMotionPlayerMonitor")
$stoppedServices = @()

if (-not $SkipServiceRestart) {
    foreach ($svcName in $services) {
        $svc = Get-Service -Name $svcName -ErrorAction SilentlyContinue
        if ($null -ne $svc -and $svc.Status -eq 'Running') {
            Write-Host "Stopping service: $svcName" -ForegroundColor Cyan
            Stop-Service -Name $svcName -Force -ErrorAction Stop
            $stoppedServices += $svcName
        }
    }

    Get-Process -Name "MotionEngine","HaptiSyncApi.WebApi","HaptiSyncCenter","HaptiSyncBgApp" -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "Note: process '$($_.Name)' is still running; the file swap may fail if it has the DLL loaded." -ForegroundColor Yellow
    }
}

New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupFile = Join-Path $backupDir "dbxService64-$stamp.dll"
Copy-Item -LiteralPath $targetDll -Destination $backupFile -Force
Write-Host "Backup written: $backupFile" -ForegroundColor Green

if (Test-Path -LiteralPath $realDll) {
    Remove-Item -LiteralPath $realDll -Force
}
Rename-Item -LiteralPath $targetDll -NewName "dbxService64_real.dll"
Copy-Item -LiteralPath $ProxyDll -Destination $targetDll -Force

Write-Host "Installed proxy:" -ForegroundColor Green
Write-Host "  $targetDll  (proxy, original FileVersion=$targetVersion, proxy FileVersion=$proxyVersion)"
Write-Host "  $realDll  (renamed original)"
Write-Host ""

if (-not $SkipServiceRestart -and $stoppedServices.Count -gt 0) {
    foreach ($svcName in $stoppedServices) {
        Write-Host "Restarting service: $svcName" -ForegroundColor Cyan
        Start-Service -Name $svcName -ErrorAction Stop
    }
}

Write-Host ""
Write-Host "Logs will be written to: $env:LOCALAPPDATA\MozaStarCitizen\dbx-trace\dbxsvc-*.log" -ForegroundColor Cyan
Write-Host "Run scripts\uninstall-dbox-service-proxy.ps1 to revert." -ForegroundColor Cyan
