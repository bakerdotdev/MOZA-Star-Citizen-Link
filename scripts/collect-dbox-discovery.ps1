param(
    [string]$OutputPath = "",
    [int]$SampleSeconds = 0,
    [switch]$EnablePacketTrace,
    [int]$PacketTraceSeconds = 0,
    [int[]]$PacketTracePorts = @(40001, 12740, 12745, 61555, 61556, 61666),
    [switch]$InstallPktMonPortFilters,
    [switch]$CaptureFirst,
    [switch]$SkipMonitoringProbe,
    [int]$MonitoringProbeTimeoutMs = 2500
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path (Get-Location) "artifacts\dbox-discovery-$stamp.txt"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

function Write-Section {
    param([string]$Title)

    Add-Content -Path $OutputPath -Value ""
    Add-Content -Path $OutputPath -Value "==== $Title ===="
}

function Write-Line {
    param([string]$Value)

    Add-Content -Path $OutputPath -Value $Value
}

function Write-ObjectText {
    param([object]$Value)

    $text = $Value | Out-String -Width 240
    Add-Content -Path $OutputPath -Value $text.TrimEnd()
}

function Test-IsElevated {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch {
        return $false
    }
}

function Get-ProcessSummary {
    param([int[]]$ProcessIds)

    foreach ($processId in ($ProcessIds | Sort-Object -Unique)) {
        try {
            $process = Get-Process -Id $processId -ErrorAction Stop
            [pscustomobject]@{
                Id = $process.Id
                ProcessName = $process.ProcessName
                Path = $process.Path
            }
        } catch {
            [pscustomobject]@{
                Id = $processId
                ProcessName = "(unavailable)"
                Path = ""
            }
        }
    }
}

function Write-FileSnapshot {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Line "missing: $Path"
        return
    }

    try {
        $file = Get-Item -LiteralPath $Path -ErrorAction Stop
        Write-ObjectText ([pscustomobject]@{
            FullName = $file.FullName
            Length = $file.Length
            LastWriteTime = $file.LastWriteTime
            FileVersion = $file.VersionInfo.FileVersion
            ProductVersion = $file.VersionInfo.ProductVersion
        })

        if ($file.Extension -match '^\.(xml|config|json|ini|txt|log)$') {
            Write-Line "Contents:"
            Get-Content -LiteralPath $file.FullName -TotalCount 120 -ErrorAction SilentlyContinue |
                ForEach-Object { Write-Line "  $_" }
        }
    } catch {
        Write-Line "failed: $Path : $($_.Exception.Message)"
    }
}

function Get-PortableStrings {
    param(
        [string]$Path,
        [int]$MinLength = 4,
        [int]$MaxLength = 180
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $strings = New-Object System.Collections.Generic.List[string]

    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
        $builder = [Text.StringBuilder]::new()

        foreach ($byte in $bytes) {
            if ($byte -ge 32 -and $byte -le 126) {
                [void]$builder.Append([char]$byte)
            } else {
                if ($builder.Length -ge $MinLength -and $builder.Length -le $MaxLength) {
                    $value = $builder.ToString().Trim()
                    if (-not [string]::IsNullOrWhiteSpace($value)) {
                        $strings.Add($value)
                    }
                }

                [void]$builder.Clear()
            }
        }

        if ($builder.Length -ge $MinLength -and $builder.Length -le $MaxLength) {
            $value = $builder.ToString().Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $strings.Add($value)
            }
        }

        [void]$builder.Clear()
        for ($index = 0; $index -lt ($bytes.Length - 1); $index += 2) {
            $codePoint = $bytes[$index] -bor ($bytes[$index + 1] -shl 8)
            if ($codePoint -ge 32 -and $codePoint -le 126) {
                [void]$builder.Append([char]$codePoint)
            } else {
                if ($builder.Length -ge $MinLength -and $builder.Length -le $MaxLength) {
                    $value = $builder.ToString().Trim()
                    if (-not [string]::IsNullOrWhiteSpace($value)) {
                        $strings.Add($value)
                    }
                }

                [void]$builder.Clear()
            }
        }

        if ($builder.Length -ge $MinLength -and $builder.Length -le $MaxLength) {
            $value = $builder.ToString().Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $strings.Add($value)
            }
        }
    } catch {
        Write-Line "String scan failed for $Path : $($_.Exception.Message)"
    }

    return @($strings | Sort-Object -Unique)
}

function Write-StringContext {
    param(
        [string[]]$Strings,
        [string]$Needle,
        [int]$Before = 8,
        [int]$After = 14
    )

    $matchIndex = -1
    for ($index = 0; $index -lt $Strings.Count; $index++) {
        if ($Strings[$index] -like "*$Needle*") {
            $matchIndex = $index
            break
        }
    }

    if ($matchIndex -lt 0) {
        Write-Line "  context '$Needle': not found"
        return
    }

    Write-Line "  context '$Needle':"
    $start = [Math]::Max(0, $matchIndex - $Before)
    $end = [Math]::Min($Strings.Count - 1, $matchIndex + $After)
    for ($index = $start; $index -le $end; $index++) {
        Write-Line "    $($Strings[$index])"
    }
}

function Write-WebAssetReferences {
    param(
        [string]$Label,
        [string]$Uri,
        [string]$Content
    )

    Write-Line "${Label}: $Uri"
    $pattern = '(?i)(https?://[^"'']+|wss?://[^"'']+|localhost:\d+|127\.0\.0\.1:\d+|/api/[A-Za-z0-9_./?&={}:@%-]+|[A-Za-z0-9_./-]*(telemetry|motion|signal|live|game|profile|setting|haptic|socket|hub)[A-Za-z0-9_./-]*)'
    $matches = @([regex]::Matches($Content, $pattern) | Select-Object -First 200)
    if ($matches.Count -eq 0) {
        Write-Line "  no endpoint-like strings found"
        return
    }

    foreach ($match in ($matches.Value | Sort-Object -Unique)) {
        Write-Line "  $match"
    }
}

function Write-DboxStarCitizenConfigSnapshot {
    Write-Section "D-BOX StarCitizen LiveMotion Config"

    $paths = @(
        "C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxInfo.xml",
        "C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxLive64.dll",
        "C:\ProgramData\D-BOX\Gaming\LiveMotion\LiveMotionConnector\LiveMotionConnector.config",
        "C:\ProgramData\D-BOX\Gaming\LiveMotion\LiveMotionConnector\LiveMotionConnector.log.config",
        "C:\Program Files\D-BOX\Gaming\Star Citizen\dbxMotionPanel.exe"
    )

    foreach ($path in $paths) {
        Write-Line ""
        Write-Line "File: $path"
        Write-FileSnapshot -Path $path
    }

    Write-Line ""
    Write-Line "Registry: HKLM:\SOFTWARE\D-BOX\PMG\StarCitizen"
    try {
        Write-ObjectText (Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\D-BOX\PMG\StarCitizen" -ErrorAction Stop |
            Select-Object Panel, PSPath)
    } catch {
        Write-Line "  unavailable: $($_.Exception.Message)"
    }
}

function Write-DboxBinarySignalHints {
    Write-Section "D-BOX StarCitizen Binary Signal Hints"

    $paths = @(
        "C:\ProgramData\D-BOX\Gaming\LiveMotion\StarCitizen\dbxLive64.dll",
        "C:\Program Files\D-BOX\Gaming\Star Citizen\dbxMotionPanel.exe"
    )

    $signalPattern = '^(MASTER_GAIN_DB|MASTER_SPECTRUM_DB|ELAPSED_TIME_MS|ACTOR_|ACCELERATION_XYZ|VELOCITY_XYZ|POSITION_XYZ|YAW_RAD|PITCH_RAD|ROLL_RAD|ANGULAR_|DYNAMIC_|VEHICLE|MAP_|ENGINE|AIRCRAFT|FLAP_|SLAT_|SPOILER_|LANDING_GEAR|CONFIG_CUSTOM|FRAME_CUSTOM|RAW_|ACTION|EVENT_|FM_|ROTOR|WEAPON_ID|DISTANCE_FROM_PLAYER|RADIUS_M|DEBRIS_QTY|ITEM_ID|MATERIAL_ID|ISRUNNING|IDENTIFIER|CINEMOTION_CONTENT_ID)'
    $noisePattern = '^(\.\?|A0x|A[0-9A-Z]{3,}|[A-Z]{20,}|[A-Za-z0-9+/]{40,})'

    foreach ($path in $paths) {
        Write-Line ""
        Write-Line "Binary: $path"
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Line "  missing"
            continue
        }

        $strings = @(Get-PortableStrings -Path $path)
        Write-Line "  portable string count: $($strings.Count)"

        $signals = @($strings |
            Where-Object { $_ -match $signalPattern -and $_ -notmatch $noisePattern } |
            Select-Object -First 240)

        if ($signals.Count -eq 0) {
            Write-Line "  no telemetry-like symbols found"
        } else {
            Write-Line "  telemetry-like symbols:"
            foreach ($signal in $signals) {
                Write-Line "    $signal"
            }
        }

        foreach ($needle in @(
            "MASTER_GAIN_DB",
            "CONFIG_UPDATE",
            "com.d-box.LiveMotion.StarCitizen.3",
            "127.0.0.1",
            "D-BOX Star Citizen Motion Code",
            "dbxMotionConfig.xml"
        )) {
            Write-StringContext -Strings $strings -Needle $needle
        }
    }
}

function Write-ProcessModuleSnapshot {
    param([object[]]$Processes)

    Write-Section "Relevant Process D-BOX Module Snapshot"

    if ($Processes.Count -eq 0) {
        Write-Line "No matching processes found."
        return
    }

    foreach ($processInfo in ($Processes | Sort-Object ProcessName, Id)) {
        Write-Line ""
        Write-Line "Process: $($processInfo.ProcessName) ($($processInfo.Id))"

        try {
            $processWithModules = Get-Process -Id $processInfo.Id -Module -ErrorAction Stop
            $modules = @($processWithModules.Modules |
                Where-Object {
                    $_.FileName -match '(?i)\\D-BOX\\|dbx|LiveMotion|HaptiSync|MotionEngine|MotionService|MonitorService|StarCitizen'
                } |
                Select-Object ModuleName, FileName, @{Name = "FileVersion"; Expression = { $_.FileVersionInfo.FileVersion } } |
                Sort-Object FileName)

            if ($modules.Count -eq 0) {
                Write-Line "  no D-BOX/StarCitizen-looking modules visible"
            } else {
                Write-ObjectText $modules
            }
        } catch {
            Write-Line "  module query failed: $($_.Exception.Message)"
        }
    }
}

function Write-RecentDboxLogTail {
    param([string[]]$Roots)

    Write-Section "Recent D-BOX Log Tail"

    if ($Roots.Count -eq 0) {
        Write-Line "No common D-BOX data directories found."
        return
    }

    $cutoff = (Get-Date).AddMinutes(-45)
    $logs = @()
    foreach ($root in $Roots) {
        try {
            $logs += @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter "*.log" -ErrorAction SilentlyContinue |
                Where-Object { $_.LastWriteTime -ge $cutoff } |
                Sort-Object LastWriteTime -Descending)
        } catch {
            Write-Line "Log scan failed for $root : $($_.Exception.Message)"
        }
    }

    $logs = @($logs | Sort-Object LastWriteTime -Descending | Select-Object -First 12)
    if ($logs.Count -eq 0) {
        Write-Line "No .log files changed since $cutoff."
        return
    }

    foreach ($log in $logs) {
        Write-Line ""
        Write-Line "Tail: $($log.FullName) (LastWriteTime=$($log.LastWriteTime), Length=$($log.Length))"
        try {
            Get-Content -LiteralPath $log.FullName -Tail 80 -ErrorAction Stop |
                ForEach-Object { Write-Line "  $_" }
        } catch {
            Write-Line "  tail failed: $($_.Exception.Message)"
        }
    }
}

function Invoke-DboxMonitoringRequest {
    param(
        [string]$CommandName,
        [string]$Request,
        [int]$TimeoutMs
    )

    $client = $null

    try {
        $client = [Net.Sockets.TcpClient]::new()
        $connectTask = $client.ConnectAsync("127.0.0.1", 40001)
        if (-not $connectTask.Wait($TimeoutMs)) {
            throw "Connection to 127.0.0.1:40001 timed out after $TimeoutMs ms."
        }

        if ($connectTask.IsFaulted) {
            throw $connectTask.Exception.InnerException
        }

        $client.SendTimeout = $TimeoutMs
        $client.ReceiveTimeout = $TimeoutMs
        $stream = $client.GetStream()
        $payload = [Text.Encoding]::ASCII.GetBytes("$Request`r`n")
        $stream.Write($payload, 0, $payload.Length)

        $buffer = New-Object byte[] 8192
        $received = New-Object System.Collections.Generic.List[byte]
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()

        while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMs -and $received.Count -lt 1048576) {
            if (-not $stream.DataAvailable) {
                Start-Sleep -Milliseconds 25
                continue
            }

            $count = $stream.Read($buffer, 0, $buffer.Length)
            if ($count -le 0) {
                break
            }

            for ($index = 0; $index -lt $count; $index++) {
                $received.Add($buffer[$index])
            }

            $text = [Text.Encoding]::UTF8.GetString($received.ToArray())
            if ($text.Contains("Cmd=`"$CommandName`"") -and $text.Contains("`r`n")) {
                $lines = @($text -split "`r`n")
                $matchingReply = @($lines | Where-Object {
                    $_.Contains("<Reply") -and $_.Contains("Cmd=`"$CommandName`"")
                } | Select-Object -First 1)

                if ($matchingReply.Count -gt 0) {
                    return $matchingReply[0]
                }
            }
        }

        if ($received.Count -eq 0) {
            return "(no bytes received before timeout)"
        }

        return [Text.Encoding]::UTF8.GetString($received.ToArray()).TrimEnd()
    } finally {
        if ($client -ne $null) {
            $client.Dispose()
        }
    }
}

function Write-MonitoringReplySummary {
    param(
        [string]$CommandName,
        [string]$Reply
    )

    if ([string]::IsNullOrWhiteSpace($Reply) -or $Reply.StartsWith("(")) {
        return
    }

    try {
        $document = [xml]$Reply
    } catch {
        Write-Line "  XML parse summary skipped: $($_.Exception.Message)"
        return
    }

    $replyNode = $document.SelectSingleNode("/Reply")
    if ($replyNode -eq $null) {
        Write-Line "  XML parse summary skipped: no Reply node found."
        return
    }

    Write-Line "  Reply error: $($replyNode.Error)"

    if ($CommandName -eq "GetLayout") {
        $commUnits = @($document.SelectNodes("//CommUnit"))
        $platforms = @($document.SelectNodes("//Platform"))
        $acms = @($document.SelectNodes("//Acm"))
        $actuators = @($document.SelectNodes("//Actuator"))

        Write-Line "  Layout counts: commUnits=$($commUnits.Count), platforms=$($platforms.Count), acms=$($acms.Count), actuators=$($actuators.Count)"
        foreach ($commUnit in ($commUnits | Select-Object -First 8)) {
            Write-Line "  CommUnit: Id='$($commUnit.Id)' Name='$($commUnit.Name)' TypeName='$($commUnit.TypeName)' Version='$($commUnit.Version)' ParameterSupport='$($commUnit.ParameterSupport)'"
        }

        foreach ($actuator in ($actuators | Select-Object -First 16)) {
            Write-Line "  Actuator: Index='$($actuator.Index)' TypeName='$($actuator.TypeName)' AxisName='$($actuator.AxisName)' Location='$($actuator.Location)' Stroke='$($actuator.Stroke)' ModelName='$($actuator.ModelName)'"
        }

        return
    }

    $fields = @($document.SelectNodes("//Field"))
    if ($fields.Count -gt 0) {
        Write-Line "  Field count: $($fields.Count)"
        $interestingFields = @($fields | Where-Object {
            "$($_.Id) $($_.Name) $($_.Description) $($_.Unit)" -match "(?i)stream|mode|motion|haptic|state|status|fault|alarm|error|position|intensity|spectrum|level|connected|active"
        } | Select-Object -First 80)

        if ($interestingFields.Count -eq 0) {
            $interestingFields = @($fields | Select-Object -First 40)
        }

        foreach ($field in $interestingFields) {
            Write-Line "  Field: Id='$($field.Id)' Name='$($field.Name)' Value='$($field.Value)' Type='$($field.Type)' Category='$($field.Category)' Unit='$($field.Unit)' Description='$($field.Description)'"
        }
    }
}

function Write-DboxMonitoringServiceProbe {
    Write-Section "D-BOX Monitoring Service XML Probe"

    if ($SkipMonitoringProbe) {
        Write-Line "Skipped by -SkipMonitoringProbe."
        return
    }

    Write-Line "Probing documented D-BOX Monitoring Service XML API at 127.0.0.1:40001."
    Write-Line "This API is expected to expose hardware/service state, not raw Star Citizen telemetry frames."

    $commands = @(
        @{ Name = "GetVersion"; Request = '<Request Cmd="GetVersion" />' },
        @{ Name = "GetLayout"; Request = '<Request Cmd="GetLayout" />' },
        @{ Name = "GetStatus"; Request = '<Request Cmd="GetStatus" />' },
        @{ Name = "GetSoftwareParameter"; Request = '<Request Cmd="GetSoftwareParameter" />' }
    )

    foreach ($command in $commands) {
        Write-Line ""
        Write-Line "Request: $($command.Request)"

        try {
            $reply = Invoke-DboxMonitoringRequest -CommandName $command.Name -Request $command.Request -TimeoutMs $MonitoringProbeTimeoutMs
            Write-Line "Reply: $reply"
            Write-MonitoringReplySummary -CommandName $command.Name -Reply $reply
        } catch {
            Write-Line "Failed: $($_.Exception.Message)"
        }
    }
}

function Invoke-PacketTrace {
    Write-Section "Optional Packet Trace"

    if (-not $EnablePacketTrace) {
        Write-Line "Disabled. To capture packet payloads/metadata, rerun from an elevated PowerShell with -EnablePacketTrace."
        Write-Line "For a port-filtered trace, also pass -InstallPktMonPortFilters. That switch temporarily clears pktmon filters."
        Write-Line "To capture Star Citizen startup traffic, also pass -CaptureFirst and launch the game while the capture is active."
        return
    }

    if (-not (Test-IsElevated)) {
        Write-Line "Skipped: pktmon packet capture requires an elevated PowerShell session."
        return
    }

    $pktmon = Get-Command pktmon -ErrorAction SilentlyContinue
    if (-not $pktmon) {
        Write-Line "Skipped: pktmon.exe was not found."
        return
    }

    $seconds = $PacketTraceSeconds
    if ($seconds -le 0) {
        if ($SampleSeconds -gt 0) {
            $seconds = $SampleSeconds
        } else {
            $seconds = 30
        }
    }

    $effectivePacketTracePorts = New-Object System.Collections.Generic.List[int]
    foreach ($port in $PacketTracePorts) {
        if ($port -gt 0 -and -not $effectivePacketTracePorts.Contains($port)) {
            $effectivePacketTracePorts.Add($port)
        }
    }

    try {
        $starCitizenProcessIds = @(Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -eq "StarCitizen" } |
            Select-Object -ExpandProperty Id)

        if ($starCitizenProcessIds.Count -gt 0) {
            $starCitizenUdpPorts = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
                Where-Object { $_.OwningProcess -in $starCitizenProcessIds -and $_.LocalPort -gt 0 } |
                Select-Object -ExpandProperty LocalPort -Unique)

            foreach ($port in $starCitizenUdpPorts) {
                if (-not $effectivePacketTracePorts.Contains([int]$port)) {
                    $effectivePacketTracePorts.Add([int]$port)
                }
            }
        }
    } catch {
        Write-Line "StarCitizen UDP port discovery failed: $($_.Exception.Message)"
    }

    $baseName = [IO.Path]::GetFileNameWithoutExtension($OutputPath)
    $traceDirectory = Split-Path -Parent $OutputPath
    if ([string]::IsNullOrWhiteSpace($traceDirectory)) {
        $traceDirectory = Get-Location
    }

    $etlPath = Join-Path $traceDirectory "$baseName-pktmon.etl"
    $txtPath = Join-Path $traceDirectory "$baseName-pktmon.txt"
    $hexPath = Join-Path $traceDirectory "$baseName-pktmon-hex.txt"
    $pcapPath = Join-Path $traceDirectory "$baseName-pktmon.pcapng"
    $statsPath = Join-Path $traceDirectory "$baseName-pktmon-stats.txt"
    $startedTrace = $false
    $installedFilters = $false

    try {
        if ($InstallPktMonPortFilters) {
            Write-Line "Installing pktmon port filters for: $($effectivePacketTracePorts -join ', ')"
            Write-Line "This removes existing pktmon filters before capture and removes these filters after capture."
            Write-ObjectText (& pktmon filter remove 2>&1)
            foreach ($port in $effectivePacketTracePorts) {
                Write-ObjectText (& pktmon filter add "moza-sc-$port" -p $port 2>&1)
            }
            $installedFilters = $true
        } else {
            Write-Line "Using current pktmon filters. No filters were installed by this script."
            Write-Line "If there are no active filters, pktmon may capture broad local traffic metadata."
        }

        Write-Line "pktmon trace ETL: $etlPath"
        Write-Line "pktmon trace text: $txtPath"
        Write-Line "pktmon trace hex text: $hexPath"
        Write-Line "pktmon trace PCAPNG: $pcapPath"
        Write-Line "pktmon trace stats: $statsPath"
        Write-Line "Capturing for $seconds second(s)."
        Write-Host "pktmon capture active for $seconds second(s). Launch Star Citizen if needed, then trigger boost, weapon fire, landing gear, and haptic events."

        $startOutput = & pktmon start --capture --comp all --pkt-size 0 --file-name $etlPath --file-size 128 --log-mode circular 2>&1
        Write-ObjectText $startOutput
        if ($LASTEXITCODE -ne 0) {
            Write-Line "pktmon start failed with exit code $LASTEXITCODE."
            return
        }

        $startedTrace = $true
        Start-Sleep -Seconds $seconds
    } finally {
        if ($startedTrace) {
            Write-Line "Stopping pktmon trace."
            Write-ObjectText (& pktmon stop 2>&1)
        }

        if ($installedFilters) {
            Write-Line "Removing pktmon filters installed for this trace."
            Write-ObjectText (& pktmon filter remove 2>&1)
        }
    }

    if (Test-Path -LiteralPath $etlPath) {
        Write-Line "Converting pktmon ETL to text."
        Write-ObjectText (& pktmon etl2txt $etlPath --out $txtPath --brief --timestamp 2>&1)
        Write-Line "Converting pktmon ETL to hex text."
        Write-ObjectText (& pktmon etl2txt $etlPath --out $hexPath --verbose --hex --timestamp 2>&1)
        Write-Line "Converting pktmon ETL to PCAPNG."
        Write-ObjectText (& pktmon etl2pcap $etlPath --out $pcapPath 2>&1)
        Write-ObjectText (& pktmon etl2txt $etlPath --out $statsPath --stats 2>&1)
    } else {
        Write-Line "pktmon ETL was not created."
    }
}

Set-Content -Path $OutputPath -Value "D-BOX / Star Citizen telemetry discovery"
Write-Line "Timestamp: $(Get-Date -Format O)"
Write-Line "Machine: $env:COMPUTERNAME"

$packetTraceAlreadyCaptured = $false
if ($CaptureFirst) {
    Write-Host "Packet capture will run before discovery probes. Launch Star Citizen and trigger haptic events while capture is active."
    Invoke-PacketTrace
    $packetTraceAlreadyCaptured = $true
}

Write-Section "HaptiSync API Swagger/OpenAPI"
$openApiCandidates = @(
    "swagger/v1/swagger.json",
    "openapi.json",
    "swagger.json",
    "v1/swagger.json"
)

foreach ($path in $openApiCandidates) {
    $uri = "http://localhost:42010/$path"
    try {
        $api = Invoke-RestMethod -Uri $uri -TimeoutSec 3
        Write-Line "FOUND $uri"
        if ($api.info) {
            Write-ObjectText $api.info
        }

        if ($api.paths) {
            $pathNames = @($api.paths.PSObject.Properties.Name | Sort-Object)
            Write-Line "Path count: $($pathNames.Count)"
            foreach ($pathName in $pathNames) {
                Write-Line "  $pathName"
            }
        } else {
            Write-Line "No paths object found."
        }
    } catch {
        Write-Line "missing $uri : $($_.Exception.Message)"
    }
}

Write-Section "HaptiSync API Index Probe"
$indexContents = @{}
foreach ($uri in @("http://localhost:42010/index.html", "http://localhost:42010/")) {
    try {
        $response = Invoke-WebRequest -Uri $uri -TimeoutSec 3 -UseBasicParsing
        Write-Line "$uri -> HTTP $($response.StatusCode), $($response.Content.Length) byte(s)"
        $indexContents[$uri] = $response.Content
    } catch {
        Write-Line "$uri -> failed: $($_.Exception.Message)"
    }
}

Write-Section "HaptiSync API Web Asset Search"
foreach ($entry in $indexContents.GetEnumerator()) {
    Write-WebAssetReferences -Label "index" -Uri $entry.Key -Content $entry.Value

    $assetMatches = @([regex]::Matches($entry.Value, '(?i)(?:src|href)=["'']([^"'']+)["'']') |
        ForEach-Object { $_.Groups[1].Value } |
        Where-Object { $_ -match '\.(js|css|html|json)(\?|$)' } |
        Sort-Object -Unique |
        Select-Object -First 40)

    foreach ($asset in $assetMatches) {
        try {
            $assetUri = [Uri]::new([Uri]$entry.Key, $asset).AbsoluteUri
            $assetResponse = Invoke-WebRequest -Uri $assetUri -TimeoutSec 3 -UseBasicParsing
            Write-WebAssetReferences -Label "asset" -Uri $assetUri -Content $assetResponse.Content
        } catch {
            Write-Line "asset $asset -> failed: $($_.Exception.Message)"
        }
    }
}

Write-Section "HaptiSync API Common Path Probe"
$commonApiPaths = @(
    "api",
    "api/status",
    "api/version",
    "api/health",
    "api/settings",
    "api/profiles",
    "api/games",
    "api/haptics",
    "api/motion",
    "api/signals",
    "api/telemetry",
    "api/live",
    "status",
    "version",
    "health"
)
foreach ($path in $commonApiPaths) {
    $uri = "http://localhost:42010/$path"
    try {
        $response = Invoke-WebRequest -Uri $uri -TimeoutSec 2 -UseBasicParsing
        Write-Line "$uri -> HTTP $($response.StatusCode), $($response.Content.Length) byte(s), content-type=$($response.Headers['Content-Type'])"
        if ($response.Content.Length -gt 0 -and $response.Content.Length -lt 4000) {
            Write-Line $response.Content
        }
    } catch {
        Write-Line "$uri -> $($_.Exception.Message)"
    }
}

Write-Section "Relevant Processes"
$processPattern = "StarCitizen|HaptiSync|DBox|D-BOX|Motion|MonitorService|MotionService|LiveMotion"
$processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -match $processPattern
})

if ($processes.Count -eq 0) {
    Write-Line "No matching processes found."
} else {
    Write-ObjectText ($processes | Select-Object Id, ProcessName, Path | Sort-Object ProcessName, Id)
}

Write-DboxStarCitizenConfigSnapshot
Write-ProcessModuleSnapshot -Processes $processes
Write-DboxBinarySignalHints

Write-Section "Known D-BOX Port Owners"
$knownPorts = @(40001, 42010, 12740, 12745, 5354, 61555, 61556, 61666)
$knownTcp = @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -in $knownPorts -or $_.RemotePort -in $knownPorts
})
$knownUdp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -in $knownPorts
})
$knownOwnerIds = @($knownTcp.OwningProcess + $knownUdp.OwningProcess) | Where-Object { $_ -gt 0 }
if ($knownOwnerIds.Count -gt 0) {
    Write-ObjectText (Get-ProcessSummary -ProcessIds $knownOwnerIds)
} else {
    Write-Line "No owners found for known D-BOX ports."
}

Write-Line "TCP:"
if ($knownTcp.Count -eq 0) {
    Write-Line "  none"
} else {
    Write-ObjectText ($knownTcp | Select-Object OwningProcess, LocalAddress, LocalPort, RemoteAddress, RemotePort, State | Sort-Object LocalPort, RemotePort, OwningProcess)
}

Write-Line "UDP:"
if ($knownUdp.Count -eq 0) {
    Write-Line "  none"
} else {
    Write-ObjectText ($knownUdp | Select-Object OwningProcess, LocalAddress, LocalPort | Sort-Object LocalPort, OwningProcess)
}

Write-DboxMonitoringServiceProbe

Write-Section "TCP Connections For Relevant Processes"
if ($processes.Count -gt 0) {
    $ids = @($processes.Id)
    $tcp = @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
        $_.OwningProcess -in $ids
    } | Select-Object OwningProcess, LocalAddress, LocalPort, RemoteAddress, RemotePort, State | Sort-Object OwningProcess, LocalPort, RemoteAddress, RemotePort)

    if ($tcp.Count -eq 0) {
        Write-Line "No TCP connections found for matching processes."
    } else {
        Write-ObjectText $tcp
    }
}

Write-Section "UDP Endpoints For Relevant Processes"
if ($processes.Count -gt 0) {
    $ids = @($processes.Id)
    $udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object {
        $_.OwningProcess -in $ids
    } | Select-Object OwningProcess, LocalAddress, LocalPort | Sort-Object OwningProcess, LocalPort)

    if ($udp.Count -eq 0) {
        Write-Line "No UDP endpoints found for matching processes."
    } else {
        Write-ObjectText $udp
    }
}

if ($SampleSeconds -gt 0) {
    Write-Section "Connection Timeline"
    Write-Line "Sampling relevant TCP/UDP state for $SampleSeconds second(s). Trigger boost, weapon fire, landing gear, and any haptic events now."
    Write-Host "Sampling TCP/UDP state for $SampleSeconds second(s). Trigger boost, weapon fire, landing gear, and haptic events now."
    $timelineTcpRecords = New-Object System.Collections.Generic.List[object]
    $timelineUdpRecords = New-Object System.Collections.Generic.List[object]

    for ($i = 0; $i -lt $SampleSeconds; $i++) {
        $timestamp = Get-Date -Format O
        Write-Line "-- sample $($i + 1)/$SampleSeconds $timestamp --"

        $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessName -match $processPattern
        })
        $ids = @($processes.Id)
        $tcp = @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
            $_.OwningProcess -in $ids -or $_.LocalPort -in $knownPorts -or $_.RemotePort -in $knownPorts
        } | Select-Object OwningProcess, LocalAddress, LocalPort, RemoteAddress, RemotePort, State | Sort-Object OwningProcess, LocalPort, RemoteAddress, RemotePort)
        $udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object {
            $_.OwningProcess -in $ids -or $_.LocalPort -in $knownPorts
        } | Select-Object OwningProcess, LocalAddress, LocalPort | Sort-Object OwningProcess, LocalPort)

        Write-Line "TCP count: $($tcp.Count)"
        Write-ObjectText $tcp
        Write-Line "UDP count: $($udp.Count)"
        Write-ObjectText $udp

        foreach ($record in $tcp) {
            $timelineTcpRecords.Add($record)
        }

        foreach ($record in $udp) {
            $timelineUdpRecords.Add($record)
        }

        Start-Sleep -Seconds 1
    }

    Write-Section "Connection Timeline Summary"
    Write-Line "TCP unique endpoints observed during sample:"
    if ($timelineTcpRecords.Count -eq 0) {
        Write-Line "  none"
    } else {
        $tcpSummary = @($timelineTcpRecords |
            Group-Object OwningProcess, LocalAddress, LocalPort, RemoteAddress, RemotePort, State |
            ForEach-Object {
                $sample = $_.Group[0]
                [pscustomobject]@{
                    SeenCount = $_.Count
                    OwningProcess = $sample.OwningProcess
                    Local = "$($sample.LocalAddress):$($sample.LocalPort)"
                    Remote = "$($sample.RemoteAddress):$($sample.RemotePort)"
                    State = $sample.State
                    IsKnownDboxPort = ($sample.LocalPort -in $knownPorts -or $sample.RemotePort -in $knownPorts)
                }
            } |
            Sort-Object IsKnownDboxPort, OwningProcess, Local, Remote, State -Descending)

        Write-ObjectText $tcpSummary
    }

    Write-Line "UDP unique endpoints observed during sample:"
    if ($timelineUdpRecords.Count -eq 0) {
        Write-Line "  none"
    } else {
        $udpSummary = @($timelineUdpRecords |
            Group-Object OwningProcess, LocalAddress, LocalPort |
            ForEach-Object {
                $sample = $_.Group[0]
                [pscustomobject]@{
                    SeenCount = $_.Count
                    OwningProcess = $sample.OwningProcess
                    Local = "$($sample.LocalAddress):$($sample.LocalPort)"
                    IsKnownDboxPort = ($sample.LocalPort -in $knownPorts)
                }
            } |
            Sort-Object IsKnownDboxPort, OwningProcess, Local -Descending)

        Write-ObjectText $udpSummary
    }
}

Write-Section "D-BOX Text Log/Config Search"
$dboxRoots = @(
    "C:\ProgramData\D-BOX",
    (Join-Path $env:LOCALAPPDATA "D-BOX"),
    (Join-Path $env:APPDATA "D-BOX")
) | Where-Object { Test-Path $_ }

if ($dboxRoots.Count -eq 0) {
    Write-Line "No common D-BOX data directories found."
} else {
    foreach ($root in $dboxRoots) {
        Write-Line "Searching $root"
        try {
            $matches = @(Get-ChildItem $root -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -match "^\.(log|txt|json|xml|ini|config)$" } |
                Select-String -Pattern "Star Citizen","StarCitizen","telemetry","localhost","127.0.0.1","udp","tcp","port","42010","40001","12740","12745","61555","61556","61666","LiveMotion","dbxLive","Game.log","ACCELERATION_XYZ","ENGINE_BOOST","WEAPON_ID" -CaseSensitive:$false -ErrorAction SilentlyContinue |
                Select-Object -First 200)

            if ($matches.Count -eq 0) {
                Write-Line "  No text matches found."
            } else {
                foreach ($match in $matches) {
                    Write-Line "  $($match.Path):$($match.LineNumber): $($match.Line.Trim())"
                }
            }
        } catch {
            Write-Line "  Search failed: $($_.Exception.Message)"
        }
    }
}

Write-RecentDboxLogTail -Roots $dboxRoots
if (-not $packetTraceAlreadyCaptured) {
    Invoke-PacketTrace
}

Write-Section "D-BOX Gaming File Inventory"
$gamingRoot = "C:\ProgramData\D-BOX\Gaming"
if (Test-Path $gamingRoot) {
    try {
        $files = @(Get-ChildItem $gamingRoot -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object FullName, Length, LastWriteTime |
            Sort-Object FullName |
            Select-Object -First 500)
        Write-ObjectText $files
    } catch {
        Write-Line "Inventory failed: $($_.Exception.Message)"
    }
} else {
    Write-Line "$gamingRoot not found."
}

Write-Section "Done"
Write-Line "Discovery output written to: $OutputPath"
Write-Host "Discovery output written to: $OutputPath"
