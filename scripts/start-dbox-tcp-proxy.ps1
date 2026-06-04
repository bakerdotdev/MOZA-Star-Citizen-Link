param(
    [int]$ListenPort = 40001,
    [int]$ForwardPort = 40002,
    [string]$LogDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $LogDir = Join-Path $repoRoot "artifacts\dbox-proxy-$stamp"
}

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

# Sanity: verify forward target is reachable, listen target is free
$forwardTest = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -eq $ForwardPort -and $_.State -eq 'Listen'
}
if (-not $forwardTest) {
    Write-Host "WARNING: nothing is listening on TCP $ForwardPort. Did you run swap-monitor-port.ps1?" -ForegroundColor Yellow
}

$listenInUse = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -eq $ListenPort -and $_.State -eq 'Listen'
}
if ($listenInUse) {
    throw "TCP $ListenPort is already in use (by PID $($listenInUse[0].OwningProcess)). Run swap-monitor-port.ps1 to move MonitorService off it first."
}

if (-not ([Management.Automation.PSTypeName]'DboxTcpProxy').Type) {
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class DboxTcpProxy {
    private readonly TcpListener _listener;
    private readonly int _forwardPort;
    private readonly string _logDir;
    private int _connCounter;
    private readonly object _consoleLock = new object();
    private readonly object _fileLock = new object();
    private volatile bool _stop;

    public DboxTcpProxy(int listenPort, int forwardPort, string logDir) {
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _forwardPort = forwardPort;
        _logDir = logDir;
    }

    public void Start() {
        Console.CancelKeyPress += OnCancelKeyPress;
        _listener.Start();
        Log("listening on 127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port +
            " -> forwarding to 127.0.0.1:" + _forwardPort);
        Log("logs in: " + _logDir);
        Log("press Ctrl-C to stop");
        while (!_stop) {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            int id = Interlocked.Increment(ref _connCounter);
            var thread = new Thread(() => HandleConnection(client, id));
            thread.IsBackground = true;
            thread.Start();
        }
        Log("listener loop exited");
    }

    private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e) {
        e.Cancel = true;
        Log("Ctrl-C received, shutting down");
        Stop();
    }

    public void Stop() {
        _stop = true;
        try { _listener.Stop(); } catch { }
    }

    private void HandleConnection(TcpClient client, int id) {
        IPEndPoint remote = null;
        string logPath = null;
        try {
            remote = (IPEndPoint)client.Client.RemoteEndPoint;
            logPath = Path.Combine(_logDir, string.Format("conn-{0:D4}-from-{1}.log", id, remote.Port));
            Log(string.Format("[conn {0:D4}] accepted from 127.0.0.1:{1}", id, remote.Port));
            AppendBytes(logPath, "META", string.Format("connection opened from 127.0.0.1:{0} at {1}", remote.Port, DateTime.Now.ToString("o")));

            using (var upstream = new TcpClient())
            using (var clientStream = client.GetStream()) {
                upstream.Connect(IPAddress.Loopback, _forwardPort);
                var upStream = upstream.GetStream();

                var t1 = Task.Run(() => Pump(clientStream, upStream, "C->S", logPath, id));
                var t2 = Task.Run(() => Pump(upStream, clientStream, "S->C", logPath, id));
                Task.WaitAny(t1, t2);

                try { clientStream.Close(); } catch { }
                try { upStream.Close(); } catch { }
            }
        } catch (Exception ex) {
            Log(string.Format("[conn {0:D4}] error: {1}", id, ex.Message));
            if (logPath != null) {
                AppendBytes(logPath, "META", "error: " + ex.Message);
            }
        } finally {
            Log(string.Format("[conn {0:D4}] closed", id));
            if (logPath != null) {
                AppendBytes(logPath, "META", "connection closed at " + DateTime.Now.ToString("o"));
            }
            try { client.Close(); } catch { }
        }
    }

    private void Pump(NetworkStream src, NetworkStream dst, string tag, string logPath, int id) {
        var buf = new byte[16384];
        try {
            int read;
            while ((read = src.Read(buf, 0, buf.Length)) > 0) {
                dst.Write(buf, 0, read);
                dst.Flush();
                LogBytes(logPath, tag, buf, read);
            }
        } catch { }
    }

    private void LogBytes(string path, string tag, byte[] buf, int len) {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var sb = new StringBuilder();
        sb.AppendLine(string.Format("[{0}] {1} ({2} bytes)", ts, tag, len));

        // Hex dump, 16 bytes per row, with ASCII gutter
        for (int offset = 0; offset < len; offset += 16) {
            int row = Math.Min(16, len - offset);
            sb.AppendFormat("  {0:x4}  ", offset);
            for (int i = 0; i < 16; i++) {
                if (i < row) sb.AppendFormat("{0:x2} ", buf[offset + i]);
                else sb.Append("   ");
                if (i == 7) sb.Append(" ");
            }
            sb.Append(" |");
            for (int i = 0; i < row; i++) {
                var c = buf[offset + i];
                sb.Append((c >= 32 && c <= 126) ? (char)c : '.');
            }
            sb.AppendLine("|");
        }
        sb.AppendLine();

        lock (_fileLock) {
            File.AppendAllText(path, sb.ToString());
        }
    }

    private void AppendBytes(string path, string tag, string msg) {
        var line = string.Format("[{0}] {1} {2}{3}", DateTime.Now.ToString("HH:mm:ss.fff"), tag, msg, Environment.NewLine);
        lock (_fileLock) {
            File.AppendAllText(path, line);
        }
    }

    private void Log(string msg) {
        var line = string.Format("{0} {1}", DateTime.Now.ToString("HH:mm:ss.fff"), msg);
        lock (_consoleLock) {
            Console.WriteLine(line);
            try {
                File.AppendAllText(Path.Combine(_logDir, "events.log"), line + Environment.NewLine);
            } catch { }
        }
    }
}
"@
}

$proxy = [DboxTcpProxy]::new($ListenPort, $ForwardPort, $LogDir)

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " D-BOX TCP proxy" -ForegroundColor Cyan
Write-Host "   listen:   127.0.0.1:$ListenPort" -ForegroundColor Cyan
Write-Host "   forward:  127.0.0.1:$ForwardPort" -ForegroundColor Cyan
Write-Host "   logs:     $LogDir" -ForegroundColor Cyan
Write-Host "------------------------------------------------------------" -ForegroundColor Cyan
Write-Host " Press Ctrl-C to stop." -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

try {
    $proxy.Start()
} finally {
    $proxy.Stop()
    Write-Host ""
    Write-Host "Proxy stopped. Logs written to:" -ForegroundColor Green
    Write-Host "  $LogDir" -ForegroundColor Green
}
