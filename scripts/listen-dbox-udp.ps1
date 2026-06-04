param(
    [int[]]$Ports = @(12741, 10012, 10013, 10014, 10015, 10016, 10017, 10018, 10019),
    [string]$OutDir = $null
)

if (-not $OutDir) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutDir = "D:\moza-star-citizen\artifacts\udp-capture-$stamp"
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "Logging to: $OutDir" -ForegroundColor Green

$listeners = @()
foreach ($p in $Ports) {
    try {
        $udp = New-Object System.Net.Sockets.UdpClient
        $udp.ExclusiveAddressUse = $false
        $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
        $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::Broadcast, $true)
        $udp.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, $p)))
        $listeners += [PSCustomObject]@{ Port = $p; Client = $udp; LogFile = (Join-Path $OutDir "udp-$p.log") }
        Write-Host "Listening on UDP $p" -ForegroundColor Cyan
    } catch {
        Write-Host "FAILED UDP $p : $_" -ForegroundColor Red
    }
}

if (-not $listeners) { Write-Host "No listeners bound. Exiting." -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Listening. Press Ctrl+C to stop." -ForegroundColor Yellow
$counter = @{}
foreach ($l in $listeners) { $counter[$l.Port] = 0 }
$lastPrint = Get-Date

try {
    while ($true) {
        foreach ($l in $listeners) {
            if ($l.Client.Available -gt 0) {
                $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
                $data = $l.Client.Receive([ref]$remote)
                $counter[$l.Port]++
                $stamp = (Get-Date -Format 'HH:mm:ss.fff')
                $hex = ($data | ForEach-Object { $_.ToString('X2') }) -join ' '
                $line = "[$stamp] from $($remote.Address):$($remote.Port) len=$($data.Length) hex=$hex"
                Add-Content -Path $l.LogFile -Value $line
            }
        }
        if (((Get-Date) - $lastPrint).TotalSeconds -ge 2) {
            $summary = ($counter.GetEnumerator() | Where-Object { $_.Value -gt 0 } | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '
            if ($summary) { Write-Host "[$([DateTime]::Now.ToString('HH:mm:ss'))] $summary" }
            $lastPrint = Get-Date
        }
        Start-Sleep -Milliseconds 5
    }
} finally {
    foreach ($l in $listeners) { try { $l.Client.Close() } catch {} }
    Write-Host ""
    Write-Host "Final counts:" -ForegroundColor Green
    $counter.GetEnumerator() | Sort-Object Key | ForEach-Object { Write-Host "  UDP $($_.Key): $($_.Value) packets" }
    Write-Host "Logs in: $OutDir" -ForegroundColor Green
}
