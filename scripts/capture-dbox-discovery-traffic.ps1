param(
    [int]$DurationSeconds = 120,
    [int[]]$Ports = @(5353, 5354, 40001),
    [string]$OutputDir = "",
    [switch]$KeepExistingFilters
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "This script must be run from an elevated (Administrator) PowerShell. pktmon requires elevation."
}

if (-not (Get-Command pktmon -ErrorAction SilentlyContinue)) {
    throw "pktmon.exe not found. It ships with Windows 10/11 - check %SystemRoot%\System32."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot = Split-Path -Parent $scriptDir
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputDir = Join-Path $repoRoot "artifacts\dbox-traffic-$stamp"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$etlPath   = Join-Path $OutputDir "trace.etl"
$txtPath   = Join-Path $OutputDir "trace-brief.txt"
$hexPath   = Join-Path $OutputDir "trace-hex.txt"
$pcapPath  = Join-Path $OutputDir "trace.pcapng"
$statsPath = Join-Path $OutputDir "trace-stats.txt"
$snapshotPath = Join-Path $OutputDir "pre-capture-snapshot.txt"

"D-BOX discovery traffic capture" | Set-Content -Path $snapshotPath
"Started: $(Get-Date -Format O)" | Add-Content -Path $snapshotPath
"Ports: $($Ports -join ', ')" | Add-Content -Path $snapshotPath
"Duration: $DurationSeconds seconds" | Add-Content -Path $snapshotPath
"" | Add-Content -Path $snapshotPath

"==== Pre-capture port owners ====" | Add-Content -Path $snapshotPath
try {
    $tcp = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $Ports }
    if ($tcp) {
        ($tcp | Select-Object OwningProcess, LocalAddress, LocalPort, State | Out-String -Width 200).TrimEnd() | Add-Content -Path $snapshotPath
    } else {
        "no TCP listeners on tracked ports" | Add-Content -Path $snapshotPath
    }
    $udp = Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $Ports }
    if ($udp) {
        ($udp | Select-Object OwningProcess, LocalAddress, LocalPort | Out-String -Width 200).TrimEnd() | Add-Content -Path $snapshotPath
    } else {
        "no UDP endpoints on tracked ports" | Add-Content -Path $snapshotPath
    }
} catch {
    "snapshot error: $($_.Exception.Message)" | Add-Content -Path $snapshotPath
}

if (-not $KeepExistingFilters) {
    Write-Host "Clearing existing pktmon filters..." -ForegroundColor Cyan
    & pktmon filter remove | Out-Null
}

foreach ($port in $Ports) {
    Write-Host "Adding pktmon filter for port $port" -ForegroundColor Cyan
    & pktmon filter add "moza-dbox-$port" -p $port | Out-Null
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host " ACTION REQUIRED: While the capture runs, do all of these:" -ForegroundColor Yellow
Write-Host "   1. Make sure HaptiSync Center is running" -ForegroundColor Yellow
Write-Host "   2. Launch Star Citizen via the RSI Launcher" -ForegroundColor Yellow
Write-Host "   3. Click 'Launch' in HaptiSync Center if it's clickable" -ForegroundColor Yellow
Write-Host "   4. Enter the PU or Arena Commander, fly around for a bit" -ForegroundColor Yellow
Write-Host "============================================================" -ForegroundColor Yellow
Write-Host ""
Write-Host "Capturing for $DurationSeconds seconds to: $etlPath" -ForegroundColor Green

$startedTrace = $false
try {
    & pktmon start --capture --comp all --pkt-size 0 --file-name $etlPath --file-size 256 --log-mode circular | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "pktmon start failed with exit code $LASTEXITCODE."
    }
    $startedTrace = $true

    $elapsed = 0
    while ($elapsed -lt $DurationSeconds) {
        $remaining = $DurationSeconds - $elapsed
        Write-Host "  capturing... $remaining s remaining" -ForegroundColor DarkGray
        Start-Sleep -Seconds ([Math]::Min(10, $remaining))
        $elapsed += 10
    }
} finally {
    if ($startedTrace) {
        Write-Host "Stopping pktmon trace..." -ForegroundColor Cyan
        & pktmon stop | Out-Null
    }
    if (-not $KeepExistingFilters) {
        Write-Host "Removing pktmon filters..." -ForegroundColor Cyan
        & pktmon filter remove | Out-Null
    }
}

if (-not (Test-Path -LiteralPath $etlPath)) {
    throw "pktmon did not produce an ETL file at $etlPath."
}

Write-Host ""
Write-Host "Converting ETL to brief text + hex text + PCAPNG..." -ForegroundColor Cyan
& pktmon etl2txt $etlPath --out $txtPath  --brief   --timestamp | Out-Null
& pktmon etl2txt $etlPath --out $hexPath  --verbose --hex --timestamp | Out-Null
& pktmon etl2pcap $etlPath --out $pcapPath | Out-Null
& pktmon etl2txt $etlPath --out $statsPath --stats | Out-Null

"" | Add-Content -Path $snapshotPath
"==== Post-capture port owners ====" | Add-Content -Path $snapshotPath
try {
    $tcp = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $Ports -or $_.RemotePort -in $Ports }
    if ($tcp) {
        ($tcp | Select-Object OwningProcess, LocalAddress, LocalPort, RemoteAddress, RemotePort, State | Out-String -Width 200).TrimEnd() | Add-Content -Path $snapshotPath
    } else {
        "no TCP connections on tracked ports" | Add-Content -Path $snapshotPath
    }
    $udp = Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $Ports }
    if ($udp) {
        ($udp | Select-Object OwningProcess, LocalAddress, LocalPort | Out-String -Width 200).TrimEnd() | Add-Content -Path $snapshotPath
    } else {
        "no UDP endpoints on tracked ports" | Add-Content -Path $snapshotPath
    }
} catch {
    "snapshot error: $($_.Exception.Message)" | Add-Content -Path $snapshotPath
}

Write-Host ""
Write-Host "Capture complete. Artifacts written to:" -ForegroundColor Green
Write-Host "  $OutputDir" -ForegroundColor Green
Write-Host ""
Write-Host "Files:" -ForegroundColor Cyan
Write-Host "  trace.etl              - raw pktmon ETL"
Write-Host "  trace-brief.txt        - one-line-per-packet summary"
Write-Host "  trace-hex.txt          - full hex dump (large)"
Write-Host "  trace.pcapng           - open in Wireshark"
Write-Host "  trace-stats.txt        - per-filter packet counts"
Write-Host "  pre-capture-snapshot.txt - port owners before + after"
Write-Host ""
Write-Host "Open trace.pcapng in Wireshark and look for:" -ForegroundColor Cyan
Write-Host "  - mDNS queries from StarCitizen.exe on UDP 5353 (filter: udp.port == 5353)"
Write-Host "  - PTR queries for D-BOX-related service types (_dbox*, _motion*, _haptisync*)"
Write-Host "  - TXT/SRV responses with swport/swver/pname records"
Write-Host "  - TCP 40001 connections (filter: tcp.port == 40001) and their XML payload"
Write-Host "  - TCP 5354 traffic (Bonjour daemon control channel)"
