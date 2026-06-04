param(
    [int]$ListenPort = 40001,
    [string]$LogDir = "",

    # Override which Virtual ACM/Actuator the synthetic layout advertises.
    # Defaults are tuned to look plausible to HaptiSync without claiming to
    # be a real shipping product.
    [string]$CommUnitId          = "MOZA-VBRIDGE-01",
    [string]$CommUnitName        = "MOZA Virtual Bridge",
    [string]$CommUnitVersion     = "1.0.0",
    # CommUnit TypeId enum (from HWMonitor.Data.CommunicationUnit):
    #   1 = KAI, 2 = KCU, 3 = Scorpion, 4 = GenericAudio.
    # Any other value displays as "Unknown" in HaptiSync.
    # KAI is what a real Haptic Bridge advertises.
    [string]$CommUnitTypeId      = "1",
    [string]$CommUnitTypeName    = "KAI",
    [string]$AcmSerial           = "MOZA-VACM-0001",
    [string]$AcmVersion          = "1.0.0",
    [string]$AcmModelName        = "ACM G3 FLEX",
    [string]$ActuatorSerial      = "MOZA-VACT-0001",
    [string]$ActuatorAxisName    = "Heave",
    [double]$ActuatorStroke      = 50.0,
    [int]$ActuatorMaxSpeed       = 100,
    [int]$ActuatorMaxAccel       = 1000,

    # Actuator model identification. The previous "Virtual" / "Virtual Actuator"
    # strings are recognized by HaptiSync's classifier as "this is a virtual rig"
    # and force SystemGeneration to UNDEFINED. Default to a real G3-era actuator
    # model from MonitorService's enum table (line 80613 of strings-ascii.txt)
    # so the classifier has a chance of mapping to a defined haptic system.
    [string]$ActuatorModel       = "AC360 AKM32D",
    [string]$ActuatorModelName   = "AC360 AKM32D",
    [string]$ActuatorEncoderType = "DSP",
    [string]$ActuatorConfiguredModel     = "AC360 AKM32D",
    [string]$ActuatorConfiguredModelName = "AC360 AKM32D",

    # ConfigurationCode appears in HaptiSync strings as "D-BOX Haptic Bridge (00000001)"
    # which suggests this maps to a known system shape (e.g., G1_KAI_1_ACTUATOR).
    # Send 1 by default so HaptiSync classifies us as a defined system rather than
    # UNDEFINED_HAPTIC_SYSTEM.
    [string]$AcmConfigurationCode = "1",

    # ACM TypeId enum (from HWMonitor.Data.ActuatorControlModule.ActuatorControlModuleType):
    #   0 = VirtualACM, 1 = ACM, 2 = ACMMaster, 3 = ACMSlave,
    #   4 = AKD, 5 = ScorpionCentral, 6 = ScorpionEndDriver, 255 = Undefined.
    # 0 (VirtualACM) makes HaptiSync's WebApi report acmTypeId=null which leaves the
    # System Generation as Unknown. Use 2 (ACMMaster) for a single-platform KAI rig.
    [string]$AcmTypeId       = "2",
    [string]$AcmTypeName     = "ACM",
    [string]$ActuatorTypeId  = "1",
    [string]$ActuatorTypeName= "Actuator"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($LogDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $LogDir = Join-Path $repoRoot "artifacts\dbox-responder-$stamp"
}

New-Item -ItemType Directory -Path $LogDir -Force | Out-Null

$listenInUse = Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object {
    $_.LocalPort -eq $ListenPort -and $_.State -eq 'Listen'
}
if ($listenInUse) {
    throw "TCP $ListenPort is already in use (by PID $($listenInUse[0].OwningProcess)). Run swap-monitor-port.ps1 to move the real MonitorService off it first."
}

if (-not ([Management.Automation.PSTypeName]'MonitorServiceResponder').Type) {
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class MonitorServiceResponder {
    private readonly TcpListener _listener;
    private readonly string _logDir;
    private readonly string _populatedLayoutInner;  // contents inside <Reply>...</Reply> for GetLayout
    private int _connCounter;
    private readonly object _consoleLock = new object();
    private readonly object _fileLock = new object();
    private volatile bool _stop;

    private readonly string _populatedStatusInner; // contents inside <Reply>...</Reply> for GetStatus

    public MonitorServiceResponder(int listenPort, string logDir, string populatedLayoutInner, string populatedStatusInner) {
        _listener = new TcpListener(IPAddress.Loopback, listenPort);
        _logDir = logDir;
        _populatedLayoutInner = populatedLayoutInner;
        _populatedStatusInner = populatedStatusInner;
    }

    public void Start() {
        Console.CancelKeyPress += OnCancelKeyPress;
        _listener.Start();
        Log("listening on 127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port);
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
            AppendLine(logPath, "META", string.Format("connection opened from 127.0.0.1:{0} at {1}", remote.Port, DateTime.Now.ToString("o")));

            using (var stream = client.GetStream()) {
                var lineBuf = new List<byte>(2048);
                var readBuf = new byte[4096];

                while (!_stop) {
                    int read;
                    try { read = stream.Read(readBuf, 0, readBuf.Length); }
                    catch (IOException) { break; }
                    if (read <= 0) break;

                    for (int i = 0; i < read; i++) {
                        lineBuf.Add(readBuf[i]);
                        // Frame terminator is \r\n. Detect when we see \n preceded by \r.
                        if (readBuf[i] == 0x0a && lineBuf.Count >= 2 && lineBuf[lineBuf.Count - 2] == 0x0d) {
                            byte[] frame = lineBuf.ToArray();
                            lineBuf.Clear();
                            LogBytes(logPath, "C->S", frame, frame.Length);

                            string text = Encoding.ASCII.GetString(frame).TrimEnd('\r', '\n');
                            byte[] reply = BuildReply(text);
                            if (reply != null && reply.Length > 0) {
                                try {
                                    stream.Write(reply, 0, reply.Length);
                                    stream.Flush();
                                    LogBytes(logPath, "S->C", reply, reply.Length);
                                } catch (IOException ioe) {
                                    AppendLine(logPath, "META", "write failed: " + ioe.Message);
                                    return;
                                }
                            }
                        }
                    }
                }
            }
        } catch (Exception ex) {
            Log(string.Format("[conn {0:D4}] error: {1}", id, ex.Message));
            if (logPath != null) AppendLine(logPath, "META", "error: " + ex.Message);
        } finally {
            Log(string.Format("[conn {0:D4}] closed", id));
            if (logPath != null) AppendLine(logPath, "META", "connection closed at " + DateTime.Now.ToString("o"));
            try { client.Close(); } catch { }
        }
    }

    private static string GetAttr(string xml, string name) {
        var m = Regex.Match(xml, name + "=\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    private byte[] BuildReply(string requestXml) {
        string cmd = GetAttr(requestXml, "Cmd");
        if (string.IsNullOrEmpty(cmd)) return null;

        string ctx = GetAttr(requestXml, "ClientContext");
        string format = GetAttr(requestXml, "Format");

        string body;
        bool includeXmlDecl = true;

        switch (cmd) {
            case "GetLayout": {
                // Populated layout: 1 CommUnit -> 1 Platform -> 1 Virtual ACM -> 1 Virtual Actuator
                var sb = new StringBuilder();
                sb.Append("<Reply Cmd=\"GetLayout\" Error=\"0\"");
                if (format != null) sb.Append(" Format=\"").Append(format).Append("\"");
                if (ctx != null) sb.Append(" ClientContext=\"").Append(ctx).Append("\"");
                sb.Append(">");
                sb.Append(_populatedLayoutInner);
                sb.Append("</Reply>");
                body = sb.ToString();
                break;
            }
            case "GetStatus": {
                var sb = new StringBuilder();
                sb.Append("<Reply Cmd=\"GetStatus\" Error=\"0\"");
                if (ctx != null) sb.Append(" ClientContext=\"").Append(ctx).Append("\"");
                sb.Append(">");
                sb.Append(_populatedStatusInner);
                sb.Append("</Reply>");
                body = sb.ToString();
                break;
            }
            case "GetSoftwareParameter": {
                // Exact byte-for-byte capture from real MonitorService
                body = "<Reply Cmd=\"GetSoftwareParameter\" Error=\"0\">"
                     + "<Field Id=\"5004\" Name=\"Local Mode\" Category=\"0\" Type=\"int\"><Default Value=\"3\" Description=\"Enable\"/></Field>"
                     + "<Field Id=\"5070\" Name=\"Intensity Reset Mode\" Category=\"0\" Type=\"int\"><Default Value=\"0\" Description=\"Disabled\"/></Field>"
                     + "<Field Id=\"6000\" Name=\"Default Mode\" Category=\"0\" Type=\"int\"><Default Value=\"0\" Description=\"Park\"/></Field>"
                     + "</Reply>";
                includeXmlDecl = false; // real server omits XML decl for this command
                break;
            }
            case "GetGenericAudioDevices": {
                string inc = GetAttr(requestXml, "IncludeNonConfiguredDevices");
                var sb = new StringBuilder();
                sb.Append("<Reply Cmd=\"GetGenericAudioDevices\" Error=\"0\"");
                if (inc != null) sb.Append(" IncludeNonConfiguredDevices=\"").Append(inc).Append("\"");
                if (ctx != null) sb.Append(" ClientContext=\"").Append(ctx).Append("\"");
                sb.Append("/>");
                body = sb.ToString();
                break;
            }
            default: {
                // Fail-open: empty success reply with whatever attributes we can echo.
                var sb = new StringBuilder();
                sb.Append("<Reply Cmd=\"").Append(cmd).Append("\" Error=\"0\"");
                if (ctx != null) sb.Append(" ClientContext=\"").Append(ctx).Append("\"");
                sb.Append("/>");
                body = sb.ToString();
                AppendLine(Path.Combine(_logDir, "unknown-commands.log"),
                    "UNKNOWN",
                    "cmd=" + cmd + " full=" + requestXml);
                break;
            }
        }

        string full = (includeXmlDecl ? "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>" : "") + body + "\r\n";
        return Encoding.ASCII.GetBytes(full);
    }

    private void LogBytes(string path, string tag, byte[] buf, int len) {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var sb = new StringBuilder();
        sb.AppendLine(string.Format("[{0}] {1} ({2} bytes)", ts, tag, len));
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
        lock (_fileLock) { File.AppendAllText(path, sb.ToString()); }
    }

    private void AppendLine(string path, string tag, string msg) {
        var line = string.Format("[{0}] {1} {2}{3}", DateTime.Now.ToString("HH:mm:ss.fff"), tag, msg, Environment.NewLine);
        lock (_fileLock) { File.AppendAllText(path, line); }
    }

    private void Log(string msg) {
        var line = string.Format("{0} {1}", DateTime.Now.ToString("HH:mm:ss.fff"), msg);
        lock (_consoleLock) {
            Console.WriteLine(line);
            try { File.AppendAllText(Path.Combine(_logDir, "events.log"), line + Environment.NewLine); } catch { }
        }
    }
}
"@
}

# Build the populated layout XML once. This is the *inner* content that goes
# between <Reply Cmd="GetLayout" Error="0" ...> and </Reply>.
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$strokeStr = $ActuatorStroke.ToString($inv)

$layoutInner =
    "<CommUnit Id=`"$CommUnitId`" Name=`"$CommUnitName`" TypeId=`"$CommUnitTypeId`" TypeName=`"$CommUnitTypeName`" Version=`"$CommUnitVersion`" AddressDescription=`"virtual`" PlatformDiscoverable=`"1`">" +
      "<Platform Index=`"0`">" +
        "<Acm Index=`"0`" Id=`"$CommUnitId-A0`" TypeId=`"$AcmTypeId`" TypeName=`"$AcmTypeName`" Version=`"$AcmVersion`" Serial=`"$AcmSerial`" ModelName=`"$AcmModelName`" AddressDescription=`"virtual`" ConfigurationCode=`"$AcmConfigurationCode`">" +
          "<Actuator Index=`"0`" Stroke=`"$strokeStr`" RawStroke=`"$strokeStr`" MaxSpeed=`"$ActuatorMaxSpeed`" MaxAcceleration=`"$ActuatorMaxAccel`" Model=`"$ActuatorModel`" ModelName=`"$ActuatorModelName`" TypeId=`"$ActuatorTypeId`" TypeName=`"$ActuatorTypeName`" Location=`"0,0`" Id=`"0`" AcmMotorSlot=`"0`" AxisName=`"$ActuatorAxisName`" Version=`"$AcmVersion`" EncoderTypeName=`"$ActuatorEncoderType`" Serial=`"$ActuatorSerial`" ConfiguredModel=`"$ActuatorConfiguredModel`" ConfiguredModelName=`"$ActuatorConfiguredModelName`"/>" +
        "</Acm>" +
      "</Platform>" +
    "</CommUnit>"

# Status reply: same nesting as layout. Components must match by Id (CommUnit)
# and Index (Platform/Acm/Actuator) so HaptiSync's DataManager binds them to
# the layout components it already created.
#
# Each component MUST carry a <Field Category="0" Id="1" Value="0"/> child.
# That maps to State.OVERALL_STATE (Id=1) inside HWMonitor.Data.Component.
# Without it, oOverallState stays NO_DEFINE (-1) which is in the error-state
# list (FAULT, NO_ACT, NO_COMM, NO_DEFINE), so HaptiSync paints the red dot.
# Value="0" = OK on State.eOverallState.
$okState = "<Field Category=`"0`" Id=`"1`" Name=`"Overall State`" Value=`"0`" Description=`"OK`" Type=`"int`"/>"

$statusInner =
    "<CommUnit Id=`"$CommUnitId`">" +
      $okState +
      "<Platform Index=`"0`">" +
        $okState +
        "<Acm Index=`"0`">" +
          $okState +
          "<Actuator Index=`"0`">" +
            $okState +
          "</Actuator>" +
        "</Acm>" +
      "</Platform>" +
    "</CommUnit>"

$responder = [MonitorServiceResponder]::new($ListenPort, $LogDir, $layoutInner, $statusInner)

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " D-BOX MonitorService synthetic responder" -ForegroundColor Cyan
Write-Host "   listen:    127.0.0.1:$ListenPort" -ForegroundColor Cyan
Write-Host "   layout:    1 CommUnit -> 1 Platform -> 1 Virtual ACM -> 1 Virtual Actuator" -ForegroundColor Cyan
Write-Host "   commUnit:  $CommUnitName ($CommUnitId)" -ForegroundColor Cyan
Write-Host "   logs:      $LogDir" -ForegroundColor Cyan
Write-Host "------------------------------------------------------------" -ForegroundColor Cyan
Write-Host " Press Ctrl-C to stop." -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""

try {
    $responder.Start()
} finally {
    $responder.Stop()
    Write-Host ""
    Write-Host "Responder stopped. Logs written to:" -ForegroundColor Green
    Write-Host "  $LogDir" -ForegroundColor Green
}
