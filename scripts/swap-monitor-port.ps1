param(
    [int]$NewPort = 40002,
    [int]$ExpectedOldPort = 40001,
    [string]$ConfigPath = "C:\Program Files\D-BOX\Monitor\MonitorService.exe.config",
    [string]$ServiceName = "DboxMotionPlayerMonitor",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell. C:\Program Files\D-BOX is write-protected."
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "MonitorService config not found: $ConfigPath"
}

$backupPath = "$ConfigPath.moza-proxy-backup"
if (Test-Path -LiteralPath $backupPath) {
    if (-not $Force) {
        throw "Backup file already exists: $backupPath. Use -Force to overwrite, or run restore-monitor-port.ps1 first."
    }
    Remove-Item -LiteralPath $backupPath -Force
}

$originalContent = Get-Content -LiteralPath $ConfigPath -Raw

$expectedMarker = "<add key=`"Port`" value=`"$ExpectedOldPort`" />"
if (-not $originalContent.Contains($expectedMarker)) {
    throw "Could not find expected marker '$expectedMarker' in $ConfigPath. Manual inspection required."
}

$newMarker = "<add key=`"Port`" value=`"$NewPort`" />"
$newContent = $originalContent.Replace($expectedMarker, $newMarker)

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    throw "Service '$ServiceName' not found."
}

$wasRunning = ($service.Status -eq 'Running')

if ($wasRunning) {
    Write-Host "Stopping service: $ServiceName" -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
}

Write-Host "Backing up original config to: $backupPath" -ForegroundColor Cyan
Copy-Item -LiteralPath $ConfigPath -Destination $backupPath -Force

Write-Host "Writing new config with Port=$NewPort" -ForegroundColor Cyan
Set-Content -LiteralPath $ConfigPath -Value $newContent -NoNewline

if ($wasRunning) {
    Write-Host "Starting service: $ServiceName" -ForegroundColor Cyan
    Start-Service -Name $ServiceName -ErrorAction Stop
}

Start-Sleep -Seconds 2

$listening = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -eq $NewPort -and $_.State -eq 'Listen'
}

if ($listening) {
    Write-Host ""
    Write-Host "MonitorService is now listening on TCP $NewPort." -ForegroundColor Green
    Write-Host "TCP $ExpectedOldPort is now free for the proxy." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "WARNING: nothing is listening on TCP $NewPort yet. The service may still be starting." -ForegroundColor Yellow
    Write-Host "Check with: Get-NetTCPConnection -LocalPort $NewPort -State Listen" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Run scripts\start-dbox-tcp-proxy.ps1 (it does NOT need elevation)"
Write-Host "  2. Launch SC + HaptiSync, play"
Write-Host "  3. Ctrl-C the proxy to stop"
Write-Host "  4. Run scripts\restore-monitor-port.ps1 (elevated) to revert"
