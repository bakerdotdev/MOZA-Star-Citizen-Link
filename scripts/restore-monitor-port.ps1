param(
    [string]$ConfigPath = "C:\Program Files\D-BOX\Monitor\MonitorService.exe.config",
    [string]$ServiceName = "DboxMotionPlayerMonitor"
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell."
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "MonitorService config not found: $ConfigPath"
}

$backupPath = "$ConfigPath.moza-proxy-backup"
if (-not (Test-Path -LiteralPath $backupPath)) {
    throw "No backup file found at: $backupPath. Nothing to restore."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    throw "Service '$ServiceName' not found."
}

$wasRunning = ($service.Status -eq 'Running')

if ($wasRunning) {
    Write-Host "Stopping service: $ServiceName" -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
}

Write-Host "Restoring config from backup: $backupPath" -ForegroundColor Cyan
Copy-Item -LiteralPath $backupPath -Destination $ConfigPath -Force
Remove-Item -LiteralPath $backupPath -Force

if ($wasRunning) {
    Write-Host "Starting service: $ServiceName" -ForegroundColor Cyan
    Start-Service -Name $ServiceName -ErrorAction Stop
}

Start-Sleep -Seconds 2

$listening = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -eq 40001 -and $_.State -eq 'Listen'
}

if ($listening) {
    Write-Host ""
    Write-Host "MonitorService is back on TCP 40001." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "WARNING: nothing is listening on TCP 40001 yet. The service may still be starting." -ForegroundColor Yellow
}
