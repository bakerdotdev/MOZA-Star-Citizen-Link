# Extract printable ASCII and UTF-16LE strings from a binary file.
# Usage: extract-binary-strings.ps1 -Path <file> [-MinLen 6] [-OutPrefix <prefix>]

param(
    [Parameter(Mandatory)] [string]$Path,
    [int]$MinLen = 6,
    [string]$OutPrefix = $null
)

if (-not (Test-Path $Path)) { Write-Error "File not found: $Path"; exit 1 }
if (-not $OutPrefix) {
    $name = [IO.Path]::GetFileNameWithoutExtension($Path)
    $OutPrefix = "D:\moza-star-citizen\artifacts\motionengine-analysis\$name"
}
New-Item -ItemType Directory -Path (Split-Path $OutPrefix) -Force | Out-Null

Write-Host "Reading $Path ..." -ForegroundColor Cyan
$bytes = [IO.File]::ReadAllBytes($Path)
Write-Host "Size: $($bytes.Length) bytes" -ForegroundColor Gray

# --- ASCII pass ---
Write-Host "Extracting ASCII (>= $MinLen)..." -ForegroundColor Cyan
$asciiOut = "$OutPrefix-strings-ascii.txt"
$sb = New-Object System.Text.StringBuilder
$asciiCount = 0
$writer = [IO.StreamWriter]::new($asciiOut, $false, [Text.Encoding]::UTF8)
try {
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $b = $bytes[$i]
        if ($b -ge 0x20 -and $b -le 0x7E) {
            [void]$sb.Append([char]$b)
        } else {
            if ($sb.Length -ge $MinLen) {
                $writer.WriteLine($sb.ToString())
                $asciiCount++
            }
            [void]$sb.Clear()
        }
    }
    if ($sb.Length -ge $MinLen) {
        $writer.WriteLine($sb.ToString())
        $asciiCount++
    }
} finally {
    $writer.Close()
}
Write-Host "ASCII strings: $asciiCount -> $asciiOut" -ForegroundColor Green

# --- UTF-16LE pass ---
Write-Host "Extracting UTF-16LE (>= $MinLen)..." -ForegroundColor Cyan
$utf16Out = "$OutPrefix-strings-utf16.txt"
$sb.Clear()
$utf16Count = 0
$writer = [IO.StreamWriter]::new($utf16Out, $false, [Text.Encoding]::UTF8)
try {
    for ($i = 0; $i -lt ($bytes.Length - 1); $i += 2) {
        $lo = $bytes[$i]; $hi = $bytes[$i + 1]
        if ($hi -eq 0 -and $lo -ge 0x20 -and $lo -le 0x7E) {
            [void]$sb.Append([char]$lo)
        } else {
            if ($sb.Length -ge $MinLen) {
                $writer.WriteLine($sb.ToString())
                $utf16Count++
            }
            [void]$sb.Clear()
        }
    }
    if ($sb.Length -ge $MinLen) {
        $writer.WriteLine($sb.ToString())
        $utf16Count++
    }
} finally {
    $writer.Close()
}
Write-Host "UTF-16 strings: $utf16Count -> $utf16Out" -ForegroundColor Green
