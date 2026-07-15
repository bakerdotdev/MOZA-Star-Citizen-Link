param(
    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [ValidateRange(1, 3600)]
    [int]$IdleTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$observerExecutable = Join-Path $repoRoot "tools\dbox-log-inspect\bin\Release\net8.0-windows\DBoxLogInspect.exe"

if (-not (Test-Path -LiteralPath $observerExecutable -PathType Leaf)) {
    throw "The Release observer is missing. Build tools\dbox-log-inspect before running this script."
}

& $observerExecutable `
    --observe $LogPath `
    --idle-timeout-seconds $IdleTimeoutSeconds

if ($LASTEXITCODE -ne 0) {
    throw "The SDK sample-log observer exited with code $LASTEXITCODE."
}
