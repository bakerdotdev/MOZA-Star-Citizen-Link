# One-shot probe: downloads ListDlls and Handle from Sysinternals if missing,
# then dumps DLL/handle tables for SC.exe and greps for D-BOX/haptic strings.
# Run from an elevated PowerShell. Requires SC to be running.

$ErrorActionPreference = 'Continue'
$tools = "D:\moza-star-citizen\tools"
$artifacts = "D:\moza-star-citizen\artifacts"

New-Item -ItemType Directory -Path $tools -Force | Out-Null
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null

function Ensure-Tool {
    param([string]$ExeName, [string]$ZipName)
    $exePath = Join-Path $tools $ExeName
    if (Test-Path $exePath) {
        Write-Host "[ok] $ExeName already present" -ForegroundColor DarkGreen
        return $exePath
    }
    $zipPath = Join-Path $tools $ZipName
    $url = "https://download.sysinternals.com/files/$ZipName"
    Write-Host "[fetch] $url" -ForegroundColor Cyan
    try {
        Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
    } catch {
        Write-Host "[fail] Download failed: $_" -ForegroundColor Red
        return $null
    }
    Write-Host "[unzip] $zipPath" -ForegroundColor Cyan
    Expand-Archive -Path $zipPath -DestinationPath $tools -Force
    if (Test-Path $exePath) {
        Write-Host "[ok] $exePath" -ForegroundColor DarkGreen
        return $exePath
    } else {
        Write-Host "[fail] $ExeName not found after unzip" -ForegroundColor Red
        return $null
    }
}

$listdlls = Ensure-Tool -ExeName "Listdlls64.exe" -ZipName "ListDlls.zip"
$handle   = Ensure-Tool -ExeName "handle64.exe"  -ZipName "Handle.zip"

if (-not $listdlls -or -not $handle) {
    Write-Host "Could not obtain Sysinternals tools. Aborting." -ForegroundColor Red
    exit 1
}

# Confirm SC is running
$sc = Get-Process StarCitizen -ErrorAction SilentlyContinue
if (-not $sc) {
    Write-Host ""
    Write-Host "ERROR: StarCitizen.exe is not running. Launch SC and re-run." -ForegroundColor Red
    exit 1
}
Write-Host ""
Write-Host "SC PID: $($sc.Id)" -ForegroundColor Yellow

# Dump DLLs
$dllOut = Join-Path $artifacts "sc-loaded-dlls.txt"
Write-Host "[dump] DLL list -> $dllOut" -ForegroundColor Cyan
& $listdlls -accepteula StarCitizen.exe 2>&1 | Out-File -FilePath $dllOut -Encoding ascii
$dllLines = (Get-Content $dllOut | Measure-Object -Line).Lines
Write-Host "[ok]   $dllLines lines written" -ForegroundColor DarkGreen

# Dump handles
$hnOut = Join-Path $artifacts "sc-handles.txt"
Write-Host "[dump] Handle list -> $hnOut" -ForegroundColor Cyan
& $handle -accepteula -p StarCitizen.exe 2>&1 | Out-File -FilePath $hnOut -Encoding ascii
$hnLines = (Get-Content $hnOut | Measure-Object -Line).Lines
Write-Host "[ok]   $hnLines lines written" -ForegroundColor DarkGreen

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " DLL matches (dbx/dbox/d-box/haptic/hemc/motion)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$dllMatches = Select-String -Path $dllOut -Pattern "dbx|dbox|d-box|haptic|hemc|motion"
if ($dllMatches) { $dllMatches | ForEach-Object { Write-Host $_.Line } }
else { Write-Host "(no matches)" -ForegroundColor DarkGray }

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Handle matches (dbx/dbox/d-box/haptic/hemc/motion/section)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
$hnMatches = Select-String -Path $hnOut -Pattern "dbx|dbox|d-box|haptic|hemc|motion"
if ($hnMatches) { $hnMatches | ForEach-Object { Write-Host $_.Line } }
else { Write-Host "(no matches)" -ForegroundColor DarkGray }

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " Done." -ForegroundColor Green
Write-Host "   DLL dump:    $dllOut ($dllLines lines)" -ForegroundColor Green
Write-Host "   Handle dump: $hnOut ($hnLines lines)" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
