# Capture every file/registry/network access SC.exe makes during startup,
# then grep for D-BOX/haptic/motion patterns. If SC probes for D-BOX at any
# level, this catches it even when EAC blocks in-process introspection.
#
# Usage:
#   1. Close StarCitizen if running.
#   2. Run this script from an ELEVATED PowerShell.
#   3. When prompted, launch SC via the RSI Launcher and play AC for ~30s.
#   4. Return to PowerShell and press ENTER to stop the capture.
#   5. Script auto-converts to CSV and prints D-BOX/haptic matches.

$ErrorActionPreference = 'Continue'
$tools = "D:\moza-star-citizen\tools"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outDir = "D:\moza-star-citizen\artifacts\sc-procmon-$stamp"

New-Item -ItemType Directory -Path $tools  -Force | Out-Null
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

# ---- 1. Acquire procmon64.exe ------------------------------------------------
$procmon = Join-Path $tools "Procmon64.exe"
if (-not (Test-Path $procmon)) {
    $zip = Join-Path $tools "ProcessMonitor.zip"
    $url = "https://download.sysinternals.com/files/ProcessMonitor.zip"
    Write-Host "[fetch] $url" -ForegroundColor Cyan
    try {
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    } catch {
        Write-Host "[fail] Download failed: $_" -ForegroundColor Red
        exit 1
    }
    Write-Host "[unzip] $zip" -ForegroundColor Cyan
    Expand-Archive -Path $zip -DestinationPath $tools -Force
    if (-not (Test-Path $procmon)) {
        Write-Host "[fail] Procmon64.exe missing after unzip" -ForegroundColor Red
        exit 1
    }
}
Write-Host "[ok] $procmon" -ForegroundColor DarkGreen

# ---- 2. Sanity check: SC must NOT be running yet -----------------------------
$sc = Get-Process StarCitizen -ErrorAction SilentlyContinue
if ($sc) {
    Write-Host ""
    Write-Host "ERROR: StarCitizen is already running (PID $($sc.Id))." -ForegroundColor Red
    Write-Host "Close it first so procmon can capture the STARTUP probes." -ForegroundColor Red
    exit 1
}

# ---- 3. Build a config file with a SC-only filter ----------------------------
# Procmon's /LoadConfig can preload a PMC file. Building one programmatically
# is messy, so we just let procmon capture everything and filter at the CSV
# step. Backing file keeps memory bounded.
$pml = Join-Path $outDir "capture.pml"
$csv = Join-Path $outDir "capture.csv"
$matches = Join-Path $outDir "filtered-matches.txt"

Write-Host ""
Write-Host "[start] Procmon -> $pml" -ForegroundColor Cyan
Write-Host "        (you may see a tray icon; that's normal)" -ForegroundColor DarkGray

# /Quiet skips EULA dialog, /AcceptEula accepts it, /Minimized starts hidden,
# /BackingFile writes to disk instead of RAM, /Runtime would auto-stop but we
# want manual control so SC startup completes first.
Start-Process -FilePath $procmon `
    -ArgumentList "/Quiet","/AcceptEula","/Minimized","/BackingFile",$pml `
    -WindowStyle Hidden

Start-Sleep -Seconds 3

# ---- 4. Prompt user ----------------------------------------------------------
Write-Host ""
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " PROCMON IS NOW CAPTURING." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " 1. Launch StarCitizen via the RSI Launcher." -ForegroundColor Yellow
Write-Host " 2. Wait for the main menu, then Enter Arena Commander." -ForegroundColor Yellow
Write-Host " 3. Fly for ~30 seconds (any ship)." -ForegroundColor Yellow
Write-Host " 4. Come back here and press ENTER to stop capture." -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host ""
Read-Host "Press ENTER when SC has booted + you've flown a bit"

# ---- 5. Stop capture ---------------------------------------------------------
Write-Host ""
Write-Host "[stop] Terminating procmon..." -ForegroundColor Cyan
& $procmon /Terminate | Out-Null
Start-Sleep -Seconds 3

if (-not (Test-Path $pml)) {
    Write-Host "[fail] PML file not created at $pml" -ForegroundColor Red
    exit 1
}
$pmlSize = (Get-Item $pml).Length / 1MB
Write-Host "[ok]   PML size: $([math]::Round($pmlSize,1)) MB" -ForegroundColor DarkGreen

# ---- 6. Convert PML -> CSV ---------------------------------------------------
Write-Host "[conv] PML -> CSV (this can take a minute on large captures)..." -ForegroundColor Cyan
& $procmon /OpenLog $pml /SaveAs $csv | Out-Null

# Procmon's /SaveAs returns immediately while the conversion happens in
# background, so wait for the CSV to actually appear.
$waited = 0
while (-not (Test-Path $csv) -and $waited -lt 120) {
    Start-Sleep -Seconds 2
    $waited += 2
}
if (-not (Test-Path $csv)) {
    Write-Host "[fail] CSV not produced after 120s" -ForegroundColor Red
    exit 1
}
$csvLines = (Get-Content $csv | Measure-Object -Line).Lines
Write-Host "[ok]   CSV: $csvLines lines" -ForegroundColor DarkGreen

# ---- 7. Filter for SC + D-BOX patterns ---------------------------------------
Write-Host ""
Write-Host "[grep] StarCitizen.exe + (dbox|d-box|dbx|haptic|hemc|motion)..." -ForegroundColor Cyan

$pattern = 'StarCitizen\.exe.*(?i)(dbox|d-box|dbx|haptic|hemc|motion)'
$hits = Select-String -Path $csv -Pattern $pattern
if ($hits) {
    $hits | ForEach-Object { $_.Line } | Out-File -FilePath $matches -Encoding utf8
    $hitCount = $hits.Count
    Write-Host "[ok]   $hitCount matching events -> $matches" -ForegroundColor DarkGreen
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Green
    Write-Host " First 30 matches:" -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Green
    $hits | Select-Object -First 30 | ForEach-Object { Write-Host $_.Line }
} else {
    "(no matches)" | Out-File -FilePath $matches -Encoding utf8
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Red
    Write-Host " ZERO matches. SC made no D-BOX/haptic/motion probes." -ForegroundColor Red
    Write-Host " This is the smoking gun: D-BOX path is not active in this" -ForegroundColor Red
    Write-Host " LIVE build, or it's hardware-gated below the OS layer." -ForegroundColor Red
    Write-Host "============================================================" -ForegroundColor Red
}

# ---- 8. Also dump SC's first 50 file opens (sanity check capture worked) -----
$scOpens = Join-Path $outDir "sc-first-file-opens.txt"
Write-Host ""
Write-Host "[bonus] Dumping first 50 SC.exe file opens for sanity..." -ForegroundColor Cyan
Select-String -Path $csv -Pattern 'StarCitizen\.exe.*CreateFile' |
    Select-Object -First 50 |
    ForEach-Object { $_.Line } |
    Out-File -FilePath $scOpens -Encoding utf8
$openCount = (Get-Content $scOpens | Measure-Object -Line).Lines
Write-Host "[ok]   $openCount lines -> $scOpens" -ForegroundColor DarkGreen

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " Done." -ForegroundColor Green
Write-Host "   PML:        $pml" -ForegroundColor Green
Write-Host "   CSV:        $csv" -ForegroundColor Green
Write-Host "   Matches:    $matches" -ForegroundColor Green
Write-Host "   SC opens:   $scOpens" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
