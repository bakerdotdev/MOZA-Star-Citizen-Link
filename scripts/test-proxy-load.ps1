param(
    [string]$ProxyDll = ""
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($ProxyDll)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $ProxyDll  = Join-Path $repoRoot "tools\dbox-proxy\out\dbxLive64.dll"
}

if (-not (Test-Path -LiteralPath $ProxyDll)) {
    Write-Host "Proxy DLL not found: $ProxyDll" -ForegroundColor Red
    Write-Host "Run tools\dbox-proxy\build.cmd first." -ForegroundColor Yellow
    exit 1
}

$tempDir = Join-Path $env:TEMP ("dbox-proxy-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
$copiedProxy = Join-Path $tempDir "dbxLive64.dll"
Copy-Item -LiteralPath $ProxyDll -Destination $copiedProxy -Force
Write-Host "Test copy: $copiedProxy" -ForegroundColor Cyan
Write-Host "(no dbxLive64_real.dll in this temp folder, so the proxy's load-real step will fail - that is expected.)" -ForegroundColor DarkGray

$logDir = Join-Path $env:LOCALAPPDATA "MozaStarCitizen\dbx-trace"
$logsBefore = @()
if (Test-Path -LiteralPath $logDir) {
    $logsBefore = @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32.dll", SetLastError=true)]
    public static extern bool FreeLibrary(IntPtr h);
}
"@

Write-Host ""
Write-Host "Calling LoadLibraryW on the proxy..." -ForegroundColor Cyan
$h = [Native]::LoadLibraryW($copiedProxy)
if ($h -eq [IntPtr]::Zero) {
    $err = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
    $msg = [System.ComponentModel.Win32Exception]::new($err).Message
    Write-Host "LoadLibrary FAILED: Win32Error=$err ($msg)" -ForegroundColor Red
    Write-Host "  Proxy is broken or its imports cannot be resolved." -ForegroundColor Yellow
} else {
    Write-Host "LoadLibrary OK: handle=$h" -ForegroundColor Green
    Start-Sleep -Milliseconds 250
    [Native]::FreeLibrary($h) | Out-Null
}

Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Checking log directory: $logDir" -ForegroundColor Cyan
if (-not (Test-Path -LiteralPath $logDir)) {
    Write-Host "  Log directory still does not exist." -ForegroundColor Yellow
    Write-Host "  -> DllMain never ran, or logger_init failed before creating the file." -ForegroundColor Yellow
    exit 2
}

$logsAfter = @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
$new = @($logsAfter | Where-Object { $logsBefore -notcontains $_.FullName })

if ($new.Count -eq 0) {
    Write-Host "  Directory exists but no NEW log files were created during this test." -ForegroundColor Yellow
    Write-Host "  -> Proxy did NOT produce a log this time. Likely DllMain failure." -ForegroundColor Yellow
    exit 2
}

Write-Host "  New log file(s) created by this test:" -ForegroundColor Green
foreach ($f in $new) {
    Write-Host ("    {0}   ({1:N0} bytes)" -f $f.FullName, $f.Length)
}
Write-Host ""
Write-Host "  --- Tail of newest log ---" -ForegroundColor Cyan
Get-Content -LiteralPath $new[0].FullName -Tail 40 -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "Proxy DllMain + logger_init work. The issue is upstream (SC isn't loading our proxy)." -ForegroundColor Green
