# Watches MotionEngine's game-facing listener ports for a NEW client connection
# (i.e. Star Citizen engaging D-BOX). EAC hides SC's side of the socket, so we
# observe from MotionEngine's side: any Established TCP on 61555/12740/12745 or
# UDP activity on 61556. Port 61555 has no baseline clients, so a connection
# there is the cleanest "SC connected" signal.
#
# No admin required. Run it, then launch SC via the RSI Launcher and fly.

param(
    [int]$Seconds = 900,
    [string]$LogDir = "D:\MOZA-Star-Citizen-Link\artifacts\gateB-watch"
)

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$log = Join-Path $LogDir ("watch-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")

function Resolve-Name($procId) {
    try { (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { "(gone)" }
}

function Emit($msg) {
    $line = "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $msg
    Write-Host $line
    Add-Content -Path $log -Value $line
}

$seen = @{}
$end = (Get-Date).AddSeconds($Seconds)
Emit "watching 61555/12740/12745 (TCP) + 61556 (UDP) for $Seconds s -> $log"
Emit "baseline: SC not expected yet. Launch SC via RSI Launcher and fly."

while ((Get-Date) -lt $end) {
    $tcp = Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -in 61555,12740,12745 -or $_.RemotePort -in 61555,12740,12745 }
    foreach ($c in $tcp) {
        $sig = "tcp {0}:{1}->{2}:{3} pid={4}" -f $c.LocalAddress,$c.LocalPort,$c.RemoteAddress,$c.RemotePort,$c.OwningProcess
        if (-not $seen.ContainsKey($sig)) {
            $seen[$sig] = $true
            Emit ("TCP  L={0}:{1}  R={2}:{3}  pid={4} ({5})" -f $c.LocalAddress,$c.LocalPort,$c.RemoteAddress,$c.RemotePort,$c.OwningProcess,(Resolve-Name $c.OwningProcess))
        }
    }

    $udp = Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalPort -in 61556,61287,61288,64090 }
    foreach ($u in $udp) {
        $sig = "udp {0}:{1} pid={2}" -f $u.LocalAddress,$u.LocalPort,$u.OwningProcess
        if (-not $seen.ContainsKey($sig)) {
            $seen[$sig] = $true
            Emit ("UDP  L={0}:{1}  pid={2} ({3})" -f $u.LocalAddress,$u.LocalPort,$u.OwningProcess,(Resolve-Name $u.OwningProcess))
        }
    }

    Start-Sleep -Milliseconds 1500
}

Emit "watch window ended"
