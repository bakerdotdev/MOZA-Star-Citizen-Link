# Unattended overnight sweep: tries many synthetic MonitorService layouts and
# detects whether any makes HaptiSync's classifier resolve a real "System
# generation" (i.e. stop logging "System generation unknown").
#
# Self-contained: launch once, no further authorization needed. Results stream
# to the results file. On a confirmed hit it STOPS and leaves that responder
# running so you wake up to an open gate.
#
# REQUIREMENT: leave HaptiSync Center OPEN (its classifier must keep polling).

param(
    [string]$Responder = "D:\MOZA-Star-Citizen-Link\scripts\start-monitor-service-responder.ps1",
    [string]$HLog = "C:\Users\jbake\AppData\Local\D-BOX\HaptiSyncCenter\Logs\HaptiSyncCenter.log",
    [string]$Results = "D:\MOZA-Star-Citizen-Link\artifacts\gen-sweep-results.txt",
    [string]$RespLog = "D:\MOZA-Star-Citizen-Link\artifacts\sweep-resp",
    [int]$SettleSeconds = 18
)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Path (Split-Path $Results) -Force | Out-Null
New-Item -ItemType Directory -Path $RespLog -Force | Out-Null

function Log($m) { $l = "$([datetimeoffset]::Now.ToString('HH:mm:ss')) $m"; Write-Host $l; Add-Content -Path $Results -Value $l }

function Stop-Resp {
    Get-NetTCPConnection -LocalPort 40001 -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object { Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
    Get-Job -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 900
}

# Count failure signals (generation-unknown AND passthrough-timeout) logged since $since.
function Eval-Window([datetimeoffset]$since) {
    $u = 0; $t = 0
    foreach ($ln in (Get-Content $HLog -Tail 350 -ErrorAction SilentlyContinue)) {
        if ($ln -notmatch 'System generation unknown' -and $ln -notmatch 'MonitorServicePassthrough') { continue }
        $parts = $ln -split '\s+', 3
        try { if ([datetimeoffset]::Parse($parts[0] + ' ' + $parts[1]) -ge $since) {
                if ($ln -match 'System generation unknown') { $u++ }
                if ($ln -match 'MonitorServicePassthrough') { $t++ } } } catch {}
    }
    , @($u, $t)
}
# Is a haptic system actually reported (not vanished)?
function Sys-Present {
    try { return ((Invoke-WebRequest 'http://localhost:42010/api/v1/haptic-systems' -TimeoutSec 4 -UseBasicParsing).Content -match '"commUnitId"') } catch { return $false }
}

# Search space (internally-plausible real D-BOX systems).
$commUnits = @(@{Id = '1'; Name = 'KAI' }, @{Id = '2'; Name = 'KCU' })
$acmTypes = @('1', '2')                                   # ACM, ACMMaster
$acmModels = @('ACM G3 FLEX', 'ACM-II', 'ACM-Lite')
$actModels = @('AC360 AKM32D', 'AC360 AKM33E', 'AC360 2BS AKM32D', 'AC330 AKM32D',
    'AC231 AKM24D', 'AC231 AKM22C', 'AC230 AKM24D', 'AC230 AKM22C',
    'AC218 AKM24D', 'AC13 AKM32D', 'AC4 AKM24D', 'AC5 AKM24D', 'AC6 AKM24D', 'AC7 AKM24D')
$cfgCodes = @('0', '1', '2', '3', '5')

$total = $commUnits.Count * $acmTypes.Count * $acmModels.Count * $actModels.Count * $cfgCodes.Count
Set-Content -Path $Results -Value "=== system-generation sweep $([datetimeoffset]::Now.ToString('o')) ==="
Log "search space: $total configs (~$([int]($total*($SettleSeconds+4)/60)) min)"
if (-not (Get-Process -Name 'HaptiSyncCenter*' -ErrorAction SilentlyContinue)) {
    Log "WARNING: HaptiSync Center is NOT running. Open it or results are meaningless."
}

$n = 0; $sawFailure = $false; $hit = $false
:outer foreach ($cu in $commUnits) {
    foreach ($at in $acmTypes) {
        foreach ($am in $acmModels) {
            foreach ($ac in $actModels) {
                foreach ($cc in $cfgCodes) {
                    $n++
                    $label = "cu=$($cu.Name)/$($cu.Id) acmType=$at acm='$am' act='$ac' cfg=$cc"
                    Remove-Item "$RespLog\*" -Recurse -Force -ErrorAction SilentlyContinue
                    Stop-Resp
                    $T = [datetimeoffset]::Now
                    $rp = @{ LogDir = $RespLog; CommUnitTypeId = $cu.Id; CommUnitTypeName = $cu.Name;
                        AcmTypeId = $at; AcmModelName = $am; ActuatorModel = $ac; ActuatorModelName = $ac; AcmConfigurationCode = $cc }
                    Start-Job -ScriptBlock { param($r, $p) & $r @p } -ArgumentList $Responder, $rp | Out-Null
                    Start-Sleep -Seconds $SettleSeconds

                    $r = Eval-Window $T
                    $unk = $r[0]; $tmo = $r[1]
                    $present = Sys-Present
                    $est = (Get-NetTCPConnection -LocalPort 40001 -State Established -ErrorAction SilentlyContinue | Measure-Object).Count
                    if ($present -and ($unk -gt 0 -or $tmo -gt 0)) { $sawFailure = $true }

                    # A real hit = system PRESENT, connected, and NO failure of either kind,
                    # after we've already seen a present-but-failing system (detection proven live).
                    if ($present -and $est -ge 2 -and $unk -eq 0 -and $tmo -eq 0 -and $sawFailure) {
                        Log "[$n/$total] CANDIDATE (present, no errors) - re-confirming 25s: $label"
                        $T2 = [datetimeoffset]::Now
                        Start-Sleep -Seconds 25
                        $r2 = Eval-Window $T2
                        if ((Sys-Present) -and $r2[0] -eq 0 -and $r2[1] -eq 0) {
                            Log "*** CONFIRMED SUCCESS [$n/$total]  $label ***"
                            try { Add-Content $Results ("API/0: " + (Invoke-WebRequest 'http://localhost:42010/api/v1/haptic-systems/0' -TimeoutSec 5 -UseBasicParsing).Content) } catch {}
                            $cmd = "-NoProfile -ExecutionPolicy Bypass -File `"$Responder`" -LogDir `"D:\MOZA-Star-Citizen-Link\artifacts\winner`" -CommUnitTypeId $($cu.Id) -CommUnitTypeName $($cu.Name) -AcmTypeId $at -AcmModelName `"$am`" -ActuatorModel `"$ac`" -ActuatorModelName `"$ac`" -AcmConfigurationCode $cc"
                            Stop-Resp
                            Start-Process powershell -WindowStyle Hidden -ArgumentList $cmd
                            Log "Detached winning responder launched (LogDir artifacts\winner) - gate should be OPEN."
                            $hit = $true; break outer
                        }
                        Log "[$n/$total] reconfirm FAILED (unk=$($r2[0]) tmo=$($r2[1])) - false alarm: $label"
                    }
                    else {
                        Log "[$n/$total] fail  present=$present unk=$unk tmo=$tmo est=$est  $label"
                    }
                }
            }
        }
    }
}

if (-not $hit) { Stop-Resp; Log "=== DONE: $total configs, none resolved the generation. Config-spoofing ruled out -> needs real hardware. ===" }
