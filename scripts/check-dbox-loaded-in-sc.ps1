param(
    [string]$ProcessName = "StarCitizen"
)

$ErrorActionPreference = "Continue"

$procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq $ProcessName })
if ($procs.Count -eq 0) {
    Write-Host "Process '$ProcessName' is not running. Launch Star Citizen, get into the game, then rerun." -ForegroundColor Yellow
    exit 1
}

foreach ($p in $procs) {
    Write-Host ""
    Write-Host "=== $($p.ProcessName) pid=$($p.Id) ===" -ForegroundColor Cyan

    try {
        $mods = @($p.Modules | Where-Object {
            $_.FileName -match '(?i)dbx|D-BOX|LiveMotion|HaptiSync|MotionService|MotionEngine'
        } | Sort-Object FileName)

        if ($mods.Count -eq 0) {
            Write-Host "  No D-BOX / dbxLive modules currently loaded." -ForegroundColor Yellow
            Write-Host "  (If the game has not actually entered a flight session yet, the SDK may not have been loaded yet.)" -ForegroundColor DarkGray
        } else {
            Write-Host "  D-BOX-related modules loaded:" -ForegroundColor Green
            foreach ($m in $mods) {
                Write-Host ("    {0}" -f $m.FileName)
                Write-Host ("      FileVersion={0}, BaseAddress={1}, ModuleMemorySize={2}" -f `
                    $m.FileVersionInfo.FileVersion, $m.BaseAddress, $m.ModuleMemorySize) -ForegroundColor DarkGray
            }
        }
    } catch {
        Write-Host "  Failed to enumerate modules: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Try running this script elevated." -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "If dbxLive64.dll appears with FullPath under C:\ProgramData\D-BOX\..." -ForegroundColor White
Write-Host "  -> Star Citizen loads it directly. File replacement (proxy install) will work." -ForegroundColor Green
Write-Host "If dbxLive64.dll appears with a different path (e.g. injected from a service):" -ForegroundColor White
Write-Host "  -> Note that path; the proxy install script needs to target it instead." -ForegroundColor Yellow
Write-Host "If no dbx module appears at all:" -ForegroundColor White
Write-Host "  -> Either the game has not yet entered a state that triggers SDK load, or" -ForegroundColor Yellow
Write-Host "     D-BOX Haptic Center / a connector service is required to inject it." -ForegroundColor Yellow
