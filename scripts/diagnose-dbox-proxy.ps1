param(
    [string]$TargetDir = "C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen"
)

$ErrorActionPreference = "Continue"

function Show-Section { param([string]$Title) Write-Host ""; Write-Host "==== $Title ====" -ForegroundColor Cyan }

Show-Section "Install location: $TargetDir"

if (-not (Test-Path -LiteralPath $TargetDir)) {
    Write-Host "  Target directory does not exist." -ForegroundColor Red
    exit 1
}

$proxyPath  = Join-Path $TargetDir "dbxLive64.dll"
$realPath   = Join-Path $TargetDir "dbxLive64_real.dll"
$backupRoot = Join-Path $TargetDir "_backup-moza-proxy"

function Show-DllInfo {
    param([string]$Label, [string]$Path)
    Write-Host "  $Label : $Path"
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "    (missing)" -ForegroundColor Yellow
        return
    }
    $item = Get-Item -LiteralPath $Path
    Write-Host ("    Length        = {0:N0} bytes" -f $item.Length)
    Write-Host ("    LastWriteTime = {0}" -f $item.LastWriteTime)
    Write-Host ("    FileVersion   = {0}" -f $item.VersionInfo.FileVersion)
    Write-Host ("    ProductName   = {0}" -f $item.VersionInfo.ProductName)
}

Show-DllInfo "dbxLive64.dll      (proxy if installed)" $proxyPath
Show-DllInfo "dbxLive64_real.dll (renamed original)  " $realPath

if (Test-Path -LiteralPath $proxyPath) {
    $size = (Get-Item -LiteralPath $proxyPath).Length
    Show-Section "Verdict on dbxLive64.dll"
    if ($size -lt 500000) {
        Write-Host "  Looks like the PROXY is installed (size $size < 500 KB)." -ForegroundColor Green
    } elseif ($size -gt 5000000) {
        Write-Host "  Looks like the ORIGINAL D-BOX DLL (size $size > 5 MB)." -ForegroundColor Yellow
        Write-Host "  -> Proxy is NOT installed. Either install didn't run or it was reverted/quarantined." -ForegroundColor Yellow
    } else {
        Write-Host "  Unexpected size ($size). Inspect manually." -ForegroundColor Yellow
    }
}

Show-Section "Backups"
if (Test-Path -LiteralPath $backupRoot) {
    Get-ChildItem -LiteralPath $backupRoot -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object Name, Length, LastWriteTime |
        Format-Table -AutoSize | Out-String -Width 240 | Write-Host
} else {
    Write-Host "  No backup folder. install-dbox-proxy.ps1 was probably never run successfully." -ForegroundColor Yellow
}

Show-Section "Log directory"
$logDir = Join-Path $env:LOCALAPPDATA "MozaStarCitizen\dbx-trace"
Write-Host "  Path: $logDir"
if (Test-Path -LiteralPath $logDir) {
    $logs = @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending)
    if ($logs.Count -eq 0) {
        Write-Host "  Directory exists but contains no log files." -ForegroundColor Yellow
        Write-Host "  -> DllMain never ran (proxy never loaded) OR proxy crashed before logger_init." -ForegroundColor Yellow
    } else {
        Write-Host "  Recent logs:" -ForegroundColor Green
        $logs | Select-Object -First 5 |
            Format-Table Name, Length, LastWriteTime -AutoSize |
            Out-String -Width 240 | Write-Host
    }
} else {
    Write-Host "  Log directory does not exist - DllMain never ran." -ForegroundColor Yellow
}

Show-Section "Windows Defender quarantine (dbx mentions)"
try {
    $threats = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object {
        $_.Resources -match '(?i)dbxLive64|MozaStarCitizen|dbox-proxy'
    })
    if ($threats.Count -eq 0) {
        Write-Host "  No threat detections mention dbxLive64 / dbox-proxy." -ForegroundColor Green
    } else {
        Write-Host "  Found Defender events touching the proxy:" -ForegroundColor Yellow
        foreach ($t in $threats) {
            Write-Host ("    {0}  resources={1}" -f $t.InitialDetectionTime, ($t.Resources -join ', '))
        }
    }
} catch {
    Write-Host "  Could not query Defender ($($_.Exception.Message))." -ForegroundColor DarkGray
}

Show-Section "Star Citizen module check"
$sc = @(Get-Process -Name "StarCitizen" -ErrorAction SilentlyContinue)
if ($sc.Count -eq 0) {
    Write-Host "  Star Citizen is not running. To verify what is currently loaded, start SC and rerun." -ForegroundColor DarkGray
} else {
    foreach ($p in $sc) {
        Write-Host "  StarCitizen pid=$($p.Id)"
        try {
            $mods = @($p.Modules | Where-Object { $_.FileName -match '(?i)dbx' } | Sort-Object FileName)
            if ($mods.Count -eq 0) {
                Write-Host "    No dbx* modules currently loaded in StarCitizen." -ForegroundColor Yellow
            } else {
                foreach ($m in $mods) {
                    Write-Host ("    {0,-60} size={1,-10} ver={2}" -f $m.FileName, $m.ModuleMemorySize, $m.FileVersionInfo.FileVersion)
                }
            }
        } catch {
            Write-Host "    Module enumeration failed: $($_.Exception.Message). Try running this script as admin." -ForegroundColor Red
        }
    }
}

Show-Section "Hints"
Write-Host "  - If 'dbxLive64.dll' size is ~6.8 MB, install did not happen or was reverted by AV. Re-run install-dbox-proxy.ps1."
Write-Host "  - If size is ~150 KB but no log was produced, DllMain failed. Check Defender quarantine; check whether the loaded module's path in SC actually points at this file."
Write-Host "  - If SC shows the proxy loaded but no log file appeared, the proxy crashed before logger_init - rebuild with verbose OutputDebugString or run DebugView."
