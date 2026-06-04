# Comprehensive probe for SC's D-BOX telemetry channel.
# Run while StarCitizen.exe is in active gameplay (Arena Commander, flight).
# Designed for one-shot use: dumps a snapshot of established TCP, UDP endpoints,
# named pipes, and any process with a handle to D-BOX/HEMC/HaptiSync resources.

param(
    [string]$OutDir = $null
)

if (-not $OutDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutDir = "D:\moza-star-citizen\artifacts\sc-telemetry-probe-$stamp"
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "Output: $OutDir" -ForegroundColor Green
Write-Host ""

# ---- 1. Confirm SC + D-BOX processes are running ----
Write-Host "=== Processes of interest ===" -ForegroundColor Cyan
$procs = Get-Process | Where-Object {
    $_.ProcessName -match 'StarCitizen|MotionEngine|HaptiSync|MonitorService|HEMotion|WebApi|BgApp'
} | Select-Object Id, ProcessName, Path
$procs | Format-Table -AutoSize | Out-String | Tee-Object -FilePath (Join-Path $OutDir 'processes.txt')

$scPid = ($procs | Where-Object ProcessName -eq 'StarCitizen').Id
$mePid = ($procs | Where-Object ProcessName -eq 'MotionEngine').Id
Write-Host "SC PID: $scPid   MotionEngine PID: $mePid" -ForegroundColor Yellow
Write-Host ""

# ---- 2. Established TCP to MotionEngine 12740 / WebApi 12745 / Monitor 40001 ----
# We view this from MotionEngine's side (LocalPort=12740) since EAC hides SC's side.
Write-Host "=== Established TCP on D-BOX listener ports (MotionEngine-side view) ===" -ForegroundColor Cyan
$dboxPorts = 12740, 12745, 40001, 42010
foreach ($p in $dboxPorts) {
    Write-Host "--- LocalPort $p ---"
    Get-NetTCPConnection -LocalPort $p -State Established -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, State, OwningProcess |
        Format-Table -AutoSize | Out-String | Tee-Object -FilePath (Join-Path $OutDir "tcp-established-$p.txt") -Append
}
Write-Host ""

# ---- 3. All UDP endpoints (listeners) on the box ----
Write-Host "=== UDP listeners (filtered to local-only and D-BOX-ish ports) ===" -ForegroundColor Cyan
Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -match '^127\.|^0\.0\.0\.0' } |
    Sort-Object LocalPort |
    Select-Object LocalAddress, LocalPort, OwningProcess |
    Format-Table -AutoSize | Out-String | Tee-Object -FilePath (Join-Path $OutDir 'udp-endpoints.txt')
Write-Host ""

# ---- 4. Full TCP table - show every loopback connection ----
Write-Host "=== ALL loopback TCP (Established) ===" -ForegroundColor Cyan
Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -match '^127\.' -or $_.RemoteAddress -match '^127\.' } |
    Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcess |
    Format-Table -AutoSize | Out-String | Tee-Object -FilePath (Join-Path $OutDir 'tcp-loopback-all.txt')
Write-Host ""

# ---- 5. Named pipes - look for D-BOX, HEMC, HaptiSync, Motion, Haptic ----
Write-Host "=== Named pipes matching D-BOX patterns ===" -ForegroundColor Cyan
$pipes = Get-ChildItem '\\.\pipe\' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'dbox|d-box|hemc|haptic|motion|haptisync|kai|acm' }
if ($pipes) {
    $pipes | Select-Object Name | Format-Table -AutoSize | Out-String | Tee-Object -FilePath (Join-Path $OutDir 'pipes-matched.txt')
} else {
    Write-Host "(no matching named pipes)"
    "(no matching pipes)" | Out-File (Join-Path $OutDir 'pipes-matched.txt')
}
Write-Host ""

# ---- 6. Dump every pipe - fallback in case naming convention is unexpected ----
Write-Host "=== ALL named pipes (full list) ===" -ForegroundColor Cyan
Get-ChildItem '\\.\pipe\' -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty Name | Sort-Object |
    Out-File (Join-Path $OutDir 'pipes-all.txt')
Write-Host "Wrote $(((Get-Content (Join-Path $OutDir 'pipes-all.txt')) | Measure-Object).Count) pipe names"
Write-Host ""

# ---- 7. handle.exe (Sysinternals) - if available, list handles owned by SC ----
$handle = Get-Command handle.exe -ErrorAction SilentlyContinue
$handle64 = Get-Command handle64.exe -ErrorAction SilentlyContinue
$h = if ($handle64) { $handle64.Source } elseif ($handle) { $handle.Source } else { $null }
if ($h -and $scPid) {
    Write-Host "=== SC handles to D-BOX-ish names (via $h) ===" -ForegroundColor Cyan
    & $h -accepteula -p $scPid 2>$null |
        Where-Object { $_ -match 'dbox|d-box|hemc|haptic|motion|haptisync|kai|acm' } |
        Tee-Object -FilePath (Join-Path $OutDir 'sc-handles-matched.txt')
} else {
    Write-Host "handle.exe not found. Install Sysinternals to see SC handles." -ForegroundColor Yellow
    "handle.exe not installed" | Out-File (Join-Path $OutDir 'sc-handles-matched.txt')
}
Write-Host ""

# ---- 8. Memory-mapped sections - look for D-BOX-named global sections ----
Write-Host "=== Global \\BaseNamedObjects with D-BOX patterns ===" -ForegroundColor Cyan
# These need WinObj or Get-CimInstance - try a PowerShell approach via .NET
try {
    Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class NtQuery {
    [DllImport("ntdll.dll")]
    public static extern int NtOpenDirectoryObject(out IntPtr h, int access, IntPtr attrs);
}
"@ -ErrorAction SilentlyContinue
} catch {}
# Easier path: just note that this requires WinObj/Process Explorer for a full sweep.
"Use WinObj.exe (Sysinternals) to scan \BaseNamedObjects for D-BOX names" |
    Out-File (Join-Path $OutDir 'sections-note.txt')
Write-Host "(Use WinObj.exe Sysinternals for shared sections - manual step)"
Write-Host ""

Write-Host "Done. Inspect: $OutDir" -ForegroundColor Green
