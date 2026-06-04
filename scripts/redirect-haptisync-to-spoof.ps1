#requires -RunAsAdministrator
<#
.SYNOPSIS
Redirect HaptiSyncBgApp's MonitorService discovery to our spoof on 40001.

.DESCRIPTION
Empirical finding: HaptiSyncBgApp.exe opens C:\Program Files\D-BOX\Monitor\MonitorService.exe.config
at startup, reads the <Port> value, and connects to that TCP port on 127.0.0.1. The companion
script swap-monitor-port.ps1 rewrote that file to Port=40002 so we could host our spoof on 40001 -
but HaptiSyncBgApp followed the real MonitorService to 40002. As a result, our spoof reaches
MotionEngine but the HaptiSync UI shows "Unknown" because the BgApp talks to the empty real
service for TypeId fields.

This script:
  1. Stops and disables the real DboxMotionPlayerMonitor service so 40001 stays free.
  2. Restores MonitorService.exe.config from the .moza-proxy-backup created by swap-monitor-port.ps1
     (or rewrites Port back to 40001 if no backup is found).
  3. Kills HaptiSyncBgApp.exe so HaptiSyncCenter.App.exe respawns it; the new instance re-reads
     the config and connects to our spoof on 40001.

Reversible via restore-monitor-port.ps1 + Set-Service -StartupType Automatic + Start-Service.
#>
param(
    [string]$ConfigPath  = 'C:\Program Files\D-BOX\Monitor\MonitorService.exe.config',
    [string]$ServiceName = 'DboxMotionPlayerMonitor',
    [int]$DesiredPort    = 40001
)

$ErrorActionPreference = 'Stop'

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (admin).'
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "MonitorService config not found: $ConfigPath"
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    throw "Service '$ServiceName' not found."
}

Write-Host "Stopping service: $ServiceName" -ForegroundColor Cyan
if ($service.Status -eq 'Running') {
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
}

Write-Host "Disabling service so it won't restart: $ServiceName" -ForegroundColor Cyan
Set-Service -Name $ServiceName -StartupType Disabled

$backupPath = "$ConfigPath.moza-proxy-backup"
if (Test-Path -LiteralPath $backupPath) {
    Write-Host "Restoring config from backup: $backupPath" -ForegroundColor Cyan
    Copy-Item -LiteralPath $backupPath -Destination $ConfigPath -Force
}
else {
    Write-Host "No backup found - rewriting Port directly to $DesiredPort" -ForegroundColor Yellow
    $content = Get-Content -LiteralPath $ConfigPath -Raw
    $newContent = [regex]::Replace($content, '<add key="Port" value="\d+" />', "<add key=`"Port`" value=`"$DesiredPort`" />")
    Set-Content -LiteralPath $ConfigPath -Value $newContent -NoNewline
}

Write-Host "Verifying config now points at port $DesiredPort..." -ForegroundColor Cyan
$verifyContent = Get-Content -LiteralPath $ConfigPath -Raw
if ($verifyContent -notmatch [regex]::Escape("<add key=`"Port`" value=`"$DesiredPort`" />")) {
    Write-Host "WARNING: config does not contain expected Port=$DesiredPort entry. Inspect $ConfigPath manually." -ForegroundColor Yellow
}
else {
    Write-Host "Config OK." -ForegroundColor Green
}

$bgApp = Get-Process -Name HaptiSyncBgApp -ErrorAction SilentlyContinue
if ($bgApp) {
    Write-Host "Killing HaptiSyncBgApp (PID $($bgApp.Id)) - HaptiSyncCenter will respawn it" -ForegroundColor Cyan
    Stop-Process -Id $bgApp.Id -Force
}
else {
    Write-Host "HaptiSyncBgApp not currently running - HaptiSyncCenter will start it on demand" -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Done. Next steps:' -ForegroundColor Green
Write-Host "  1. Confirm the spoof responder is still listening on 127.0.0.1:$DesiredPort."
Write-Host '  2. Open HaptiSync Center (or wait for HaptiSyncCenter.App.exe to relaunch BgApp).'
Write-Host '  3. Verify the device label changes from "Unknown" to "KAI: MOZA Virtual Bridge".'
Write-Host ''
Write-Host 'To revert:'
Write-Host "  Set-Service -Name $ServiceName -StartupType Automatic"
Write-Host "  Start-Service -Name $ServiceName"
Write-Host '  (and rerun swap-monitor-port.ps1 if you want the real service back on 40002)'
