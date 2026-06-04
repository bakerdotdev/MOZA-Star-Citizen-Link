param(
    [int]$PacketTraceSeconds = 180,
    [int]$SampleSeconds = 60
)

$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDirectory
$discoveryScript = Join-Path $scriptDirectory "collect-dbox-discovery.ps1"

if (-not (Test-Path -LiteralPath $discoveryScript)) {
    throw "Discovery script not found: $discoveryScript"
}

$argumentList = @(
    "-NoExit",
    "-ExecutionPolicy", "Bypass",
    "-File", "`"$discoveryScript`"",
    "-CaptureFirst",
    "-EnablePacketTrace",
    "-InstallPktMonPortFilters",
    "-PacketTraceSeconds", $PacketTraceSeconds,
    "-SampleSeconds", $SampleSeconds
)

Write-Host "Opening elevated D-BOX discovery capture window."
Write-Host "When the elevated window opens, launch Star Citizen and trigger engine, boost, weapon, gear, countermeasure, and impact events while capture is active."

Start-Process -FilePath "powershell.exe" -Verb RunAs -WorkingDirectory $repoRoot -ArgumentList $argumentList
