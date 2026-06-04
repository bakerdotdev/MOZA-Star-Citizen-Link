param(
    [string]$OutputPath = "",
    [int]$DurationSeconds = 240,
    [int[]]$Ports = @(61666),
    [int]$MaxDumpBytes = 4096
)

$ErrorActionPreference = "Continue"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path (Get-Location) "artifacts\dbox-receiver-probe-$stamp.txt"
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

function Write-ProbeLine {
    param([string]$Value)

    Add-Content -Path $OutputPath -Value $Value
}

function Convert-BytesToAsciiPreview {
    param([byte[]]$Bytes)

    $builder = [Text.StringBuilder]::new()
    foreach ($byte in $Bytes) {
        if ($byte -ge 32 -and $byte -le 126) {
            [void]$builder.Append([char]$byte)
        } elseif ($byte -eq 9) {
            [void]$builder.Append("\t")
        } elseif ($byte -eq 10) {
            [void]$builder.Append("\n")
        } elseif ($byte -eq 13) {
            [void]$builder.Append("\r")
        } else {
            [void]$builder.Append(".")
        }
    }

    return $builder.ToString()
}

function Write-Bytes {
    param(
        [string]$Protocol,
        [int]$Port,
        [string]$Remote,
        [byte[]]$Bytes
    )

    $timestamp = Get-Date -Format O
    $dumpBytes = if ($Bytes.Length -gt $MaxDumpBytes) {
        $Bytes[0..($MaxDumpBytes - 1)]
    } else {
        $Bytes
    }

    $truncated = if ($Bytes.Length -gt $MaxDumpBytes) {
        " truncated=$($Bytes.Length - $MaxDumpBytes)"
    } else {
        ""
    }

    $hex = [BitConverter]::ToString($dumpBytes).Replace("-", " ")
    $ascii = Convert-BytesToAsciiPreview -Bytes $dumpBytes

    Write-ProbeLine "[$timestamp] $Protocol port=$Port remote=$Remote bytes=$($Bytes.Length)$truncated"
    Write-ProbeLine "  ascii: $ascii"
    Write-ProbeLine "  hex: $hex"
}

function Write-PortOwners {
    param([int[]]$ProbePorts)

    Write-ProbeLine ""
    Write-ProbeLine "==== Current Port Owners ===="

    foreach ($port in $ProbePorts) {
        Write-ProbeLine "Port $port TCP:"
        $tcp = @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
            Select-Object OwningProcess, LocalAddress, LocalPort, RemoteAddress, RemotePort, State)
        if ($tcp.Count -eq 0) {
            Write-ProbeLine "  none"
        } else {
            Write-ProbeLine (($tcp | Format-Table -AutoSize | Out-String -Width 240).TrimEnd())
        }

        Write-ProbeLine "Port $port UDP:"
        $udp = @(Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
            Select-Object OwningProcess, LocalAddress, LocalPort)
        if ($udp.Count -eq 0) {
            Write-ProbeLine "  none"
        } else {
            Write-ProbeLine (($udp | Format-Table -AutoSize | Out-String -Width 240).TrimEnd())
        }
    }
}

Set-Content -Path $OutputPath -Value "D-BOX receiver port probe"
Write-ProbeLine "Timestamp: $(Get-Date -Format O)"
Write-ProbeLine "Machine: $env:COMPUTERNAME"
Write-ProbeLine "DurationSeconds: $DurationSeconds"
Write-ProbeLine "Ports: $($Ports -join ', ')"
Write-ProbeLine "Bind address: 127.0.0.1"
Write-ProbeLine ""
Write-ProbeLine "Close Star Citizen, start this probe, then launch Star Citizen and trigger D-BOX haptic events."
Write-ProbeLine "This script only listens on the configured LiveMotion receiver port and logs incoming TCP/UDP bytes."

Write-PortOwners -ProbePorts $Ports

$tcpListeners = New-Object System.Collections.Generic.List[object]
$udpClients = New-Object System.Collections.Generic.List[object]
$tcpClients = New-Object System.Collections.Generic.List[object]
$totalTcpBytes = 0
$totalUdpBytes = 0
$totalTcpConnections = 0
$totalUdpDatagrams = 0

foreach ($port in ($Ports | Sort-Object -Unique)) {
    try {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        $listener.Start()
        $tcpListeners.Add([pscustomobject]@{ Port = $port; Listener = $listener })
        Write-ProbeLine "TCP listener started on 127.0.0.1:$port"
    } catch {
        Write-ProbeLine "TCP listener failed on 127.0.0.1:$port : $($_.Exception.Message)"
    }

    try {
        $udp = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
        $udp.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, $port))
        $udpClients.Add([pscustomobject]@{ Port = $port; Client = $udp })
        Write-ProbeLine "UDP listener started on 127.0.0.1:$port"
    } catch {
        Write-ProbeLine "UDP listener failed on 127.0.0.1:$port : $($_.Exception.Message)"
    }
}

$deadline = [DateTimeOffset]::Now.AddSeconds($DurationSeconds)
Write-Host "D-BOX receiver probe active for $DurationSeconds second(s). Output: $OutputPath"
Write-Host "Launch Star Citizen now and trigger engine, boost, weapon, gear, countermeasure, and impact events."

try {
    while ([DateTimeOffset]::Now -lt $deadline) {
        foreach ($entry in $tcpListeners.ToArray()) {
            try {
                while ($entry.Listener.Pending()) {
                    $client = $entry.Listener.AcceptTcpClient()
                    $client.NoDelay = $true
                    $remote = $client.Client.RemoteEndPoint.ToString()
                    $tcpClients.Add([pscustomobject]@{
                        Port = $entry.Port
                        Client = $client
                        Stream = $client.GetStream()
                        Remote = $remote
                    })
                    $totalTcpConnections++
                    Write-ProbeLine "[$(Get-Date -Format O)] TCP accepted port=$($entry.Port) remote=$remote"
                }
            } catch {
                Write-ProbeLine "TCP accept failed on port $($entry.Port): $($_.Exception.Message)"
            }
        }

        for ($index = $tcpClients.Count - 1; $index -ge 0; $index--) {
            $entry = $tcpClients[$index]
            try {
                $stream = $entry.Stream
                if ($stream.DataAvailable) {
                    $buffer = New-Object byte[] 8192
                    $count = $stream.Read($buffer, 0, $buffer.Length)
                    if ($count -le 0) {
                        $entry.Client.Dispose()
                        $tcpClients.RemoveAt($index)
                        continue
                    }

                    $bytes = New-Object byte[] $count
                    [Array]::Copy($buffer, $bytes, $count)
                    $totalTcpBytes += $count
                    Write-Bytes -Protocol "TCP" -Port $entry.Port -Remote $entry.Remote -Bytes $bytes
                }
            } catch {
                Write-ProbeLine "TCP read closed port=$($entry.Port) remote=$($entry.Remote): $($_.Exception.Message)"
                try {
                    $entry.Client.Dispose()
                } catch {
                }
                $tcpClients.RemoveAt($index)
            }
        }

        foreach ($entry in $udpClients.ToArray()) {
            try {
                while ($entry.Client.Available -gt 0) {
                    $remoteEndPoint = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
                    $bytes = $entry.Client.Receive([ref]$remoteEndPoint)
                    $totalUdpBytes += $bytes.Length
                    $totalUdpDatagrams++
                    Write-Bytes -Protocol "UDP" -Port $entry.Port -Remote $remoteEndPoint.ToString() -Bytes $bytes
                }
            } catch {
                Write-ProbeLine "UDP receive failed on port $($entry.Port): $($_.Exception.Message)"
            }
        }

        Start-Sleep -Milliseconds 25
    }
} finally {
    foreach ($entry in $tcpClients.ToArray()) {
        try {
            $entry.Client.Dispose()
        } catch {
        }
    }

    foreach ($entry in $tcpListeners.ToArray()) {
        try {
            $entry.Listener.Stop()
        } catch {
        }
    }

    foreach ($entry in $udpClients.ToArray()) {
        try {
            $entry.Client.Dispose()
        } catch {
        }
    }
}

Write-ProbeLine ""
Write-ProbeLine "==== Summary ===="
Write-ProbeLine "TCP connections: $totalTcpConnections"
Write-ProbeLine "TCP bytes: $totalTcpBytes"
Write-ProbeLine "UDP datagrams: $totalUdpDatagrams"
Write-ProbeLine "UDP bytes: $totalUdpBytes"
Write-ProbeLine "Finished: $(Get-Date -Format O)"
Write-Host "D-BOX receiver probe finished. Output: $OutputPath"
