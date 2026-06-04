param(
    [string]$TargetDir = "C:\ProgramData\D-BOX\MotionService",
    [switch]$SkipServiceRestart
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $TargetDir)) {
    throw "Target directory not found: $TargetDir"
}

$targetDll = Join-Path $TargetDir "dbxService64.dll"
$realDll   = Join-Path $TargetDir "dbxService64_real.dll"

if (-not (Test-Path -LiteralPath $realDll)) {
    Write-Host "No proxy install detected (dbxService64_real.dll not found in $TargetDir)." -ForegroundColor Yellow
    exit 0
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
}

if (Test-Path -LiteralPath $targetDll) {
    Remove-Item -LiteralPath $targetDll -Force
}
Rename-Item -LiteralPath $realDll -NewName "dbxService64.dll"

Write-Host "Restored original dbxService64.dll in $TargetDir" -ForegroundColor Green

if (-not $SkipServiceRestart -and $stoppedServices.Count -gt 0) {
    foreach ($svcName in $stoppedServices) {
        Write-Host "Restarting service: $svcName" -ForegroundColor Cyan
        Start-Service -Name $svcName -ErrorAction Stop
    }
}
