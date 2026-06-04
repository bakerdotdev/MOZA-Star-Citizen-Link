# Loopback packet capture for Star Citizen <-> D-BOX traffic discovery.
#
# Captures TCP/UDP on the D-BOX listener ports (40001/12740/12745/42010) and on
# SC's observed UDP listeners (61287/61288/64090) plus MotionEngine's UDP 61556.
# Anything SC sends or receives on these ports will land in the ETL/TXT output.
#
# Run from an *elevated* PowerShell (pktmon requires Administrator).
# Default capture window is 90 seconds; pass -Seconds N to override.
#
# Workflow:
#   1. Start the D-BOX MonitorService responder in another window (already
#      green dot in HaptiSync).
#   2. Launch SC, get into AC, spawn a ship, but stay in the hangar/menu.
#   3. In an elevated PowerShell:
#        powershell -ExecutionPolicy Bypass -File scripts\capture-sc-loopback.ps1
#   4. When prompted, tab to SC and start flying. Capture auto-stops after the
#      configured duration.

param(
    [int]$Seconds = 90,
    [string]$OutDir = $null
)

# pktmon writes informational stderr lines like "Packet Monitor is not running."
# which PowerShell escalates to NativeCommandError. Use Continue so those
# benign messages don't abort the script; we check $LASTEXITCODE where it matters.
$ErrorActionPreference = 'Continue'

# Require Administrator
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "ERROR: This script must be run as Administrator." -ForegroundColor Red
    Write-Host "Right-click PowerShell -> 'Run as administrator', then re-run." -ForegroundColor Red
    exit 1
}

if (-not $OutDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutDir = "D:\moza-star-citizen\artifacts\sc-loopback-capture-$stamp"
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

$etlFile = Join-Path $OutDir "capture.etl"
$txtFile = Join-Path $OutDir "capture.txt"
$logFile = Join-Path $OutDir "events.log"

function Log($msg) {
    $ts = Get-Date -Format "HH:mm:ss.fff"
    $line = "[$ts] $msg"
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

Log "Output: $OutDir"

# Clean any prior pktmon state
Log "Resetting pktmon..."
& pktmon stop 2>&1 | Out-Null
& pktmon filter remove 2>&1 | Out-Null

# Filter to relevant ports (both directions captured automatically).
$ports = @(
    @{p=40001; t='TCP'; name='dbox-monitor'},
    @{p=12740; t='TCP'; name='dbox-me-12740'},
    @{p=12745; t='TCP'; name='dbox-me-12745'},
    @{p=42010; t='TCP'; name='dbox-webapi'},
    @{p=61556; t='UDP'; name='me-udp-61556'},
    @{p=61287; t='UDP'; name='sc-udp-61287'},
    @{p=61288; t='UDP'; name='sc-udp-61288'},
    @{p=64090; t='UDP'; name='sc-udp-64090'}
)

foreach ($f in $ports) {
    Log "Filter: $($f.name) ($($f.t) port $($f.p))"
    $result = & pktmon filter add $f.name -t $f.t -p $f.p 2>&1
    if ($LASTEXITCODE -ne 0) {
        Log "  WARN: filter add returned $LASTEXITCODE : $result"
    }
}

Log "Starting pktmon capture (all components, full packet size)..."
$startOutput = & pktmon start --capture --comp all --pkt-size 0 -f $etlFile 2>&1
$startOutput | Out-File -FilePath (Join-Path $OutDir "pktmon-start.log")
if ($LASTEXITCODE -ne 0) {
    Log "ERROR: pktmon start exited $LASTEXITCODE"
    Log "Output: $startOutput"
    exit 1
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " Capture starting in 5 seconds. TAB TO STAR CITIZEN NOW." -ForegroundColor Yellow
Write-Host " Capture will auto-stop after $Seconds seconds." -ForegroundColor Yellow
Write-Host " Do NOT alt-tab back until done (cruise control will fire)." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host ""

for ($i = 5; $i -ge 1; $i--) {
    Write-Host "  $i..." -ForegroundColor Cyan
    Start-Sleep -Seconds 1
}

Log "Capturing for $Seconds seconds. Fly!"
Start-Sleep -Seconds $Seconds

Log "Stopping capture..."
$stopOutput = & pktmon stop 2>&1
$stopOutput | Out-File -FilePath (Join-Path $OutDir "pktmon-stop.log")

Log "Decoding ETL -> TXT..."
$decodeOutput = & pktmon etl2txt $etlFile -o $txtFile 2>&1
$decodeOutput | Out-File -FilePath (Join-Path $OutDir "pktmon-decode.log")

if (Test-Path $txtFile) {
    $lineCount = (Get-Content $txtFile | Measure-Object -Line).Lines
    $etlSize = (Get-Item $etlFile).Length
    Log "Decoded $lineCount lines. ETL size: $etlSize bytes."
} else {
    Log "WARNING: No TXT output. Check pktmon-decode.log."
}

# Quick summary - count packets per port
if (Test-Path $txtFile) {
    Log "Per-port packet counts:"
    foreach ($f in $ports) {
        $count = (Select-String -Path $txtFile -Pattern ":$($f.p)" -SimpleMatch -ErrorAction SilentlyContinue | Measure-Object).Count
        Log "  $($f.t) $($f.p) ($($f.name)): $count matches"
    }
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " Capture complete." -ForegroundColor Green
Write-Host "   ETL: $etlFile" -ForegroundColor Green
Write-Host "   TXT: $txtFile" -ForegroundColor Green
Write-Host "   Log: $logFile" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green

# Cleanup filters so we don't leave state around
& pktmon filter remove 2>&1 | Out-Null
