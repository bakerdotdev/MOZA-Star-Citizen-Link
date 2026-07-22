param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [ValidateRange(0, 100)]
    [double]$Speed = 1,

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

$candidatePath = [System.IO.Path]::GetFullPath($LogPath)
if ($candidatePath.StartsWith("\\")) {
    throw "Network and device paths are not accepted. Copy the sample log to a local drive."
}

$pathRoot = [System.IO.Path]::GetPathRoot($candidatePath)
if ($pathRoot -and
    [System.IO.DriveInfo]::new($pathRoot).DriveType -eq
        [System.IO.DriveType]::Network) {
    throw "Mapped network drives are not accepted. Copy the sample log to a local drive."
}

$resolvedLog = (Resolve-Path -LiteralPath $LogPath).Path
$initializeLine = Get-Content -LiteralPath $resolvedLog -TotalCount 20 |
    Where-Object { $_ -match '<Initialize\b' } |
    Select-Object -First 1

if (-not $initializeLine) {
    throw "No D-BOX Initialize record was found near the start of: $resolvedLog"
}

$appKeyMatch = [regex]::Match(
    $initializeLine,
    'AppKey="(SampleRacer|SampleFlyer)"',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

if (-not $appKeyMatch.Success) {
    throw "Only logs self-identifying as SampleRacer or SampleFlyer are accepted."
}

$appKey = $appKeyMatch.Groups[1].Value

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$appExecutable = Join-Path $repoRoot "src\MozaStarCitizen.App\bin\Release\net8.0-windows\MozaStarCitizen.exe"
$inspectorExecutable = Join-Path $repoRoot "tools\dbox-log-inspect\bin\Release\net8.0-windows\DBoxLogInspect.exe"

if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $inspectorExecutable -PathType Leaf)) {
    throw "Release binaries are missing. Build the app and tools\dbox-log-inspect before running this offline launcher."
}

& $inspectorExecutable --validate $resolvedLog
if ($LASTEXITCODE -ne 0) {
    throw "The offline sample-log validator rejected the selected file."
}

$env:MOZA_SC_TELEMETRY = "DBoxSdkSampleLog"
$env:MOZA_SC_DBOX_XML_LOG = $resolvedLog
$env:MOZA_SC_DBOX_REPLAY_SPEED = $Speed.ToString(
    [System.Globalization.CultureInfo]::InvariantCulture)
$env:MOZA_SC_OUTPUT = "Preview"

Write-Host "D-BOX SDK sample replay" -ForegroundColor Cyan
Write-Host "  Log:    $resolvedLog"
Write-Host "  AppKey: $appKey"
Write-Host "  Mode:   Replay"
Write-Host "  Speed:  $env:MOZA_SC_DBOX_REPLAY_SPEED"
Write-Host "  Output: $env:MOZA_SC_OUTPUT (visualization-only; hardware disabled)"
Write-Host ""
Write-Host "The AppKey is self-asserted and is not a cryptographic proof of provenance." -ForegroundColor DarkGray
Write-Host "This reads only the selected local sample log. It does not access Star Citizen, EAC, D-BOX services, handlers, registry, or network endpoints." -ForegroundColor DarkGray

if ($ValidateOnly) {
    Write-Host "Validation succeeded; the app was not launched." -ForegroundColor Green
    return
}

& $appExecutable
if ($LASTEXITCODE -ne 0) {
    throw "The replay app exited with code $LASTEXITCODE."
}
