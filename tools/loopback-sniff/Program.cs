using System.Net;
using System.Net.Sockets;
using System.Text;

// Minimal Windows loopback (127.0.0.1) sniffer for D-BOX telemetry capture.
// Uses a raw socket + SIO_RCVALL. Requires Administrator.
//
// It COUNTS every loopback TCP/UDP flow (so port 40001 acts as a built-in
// "is loopback capture even working?" check, and unexpected SC ports get
// discovered), but only HEX-DUMPS the SC-telemetry candidate ports.
//
// Usage:  LoopbackSniff.exe [-s <seconds>] <outDir> [extraDumpPort ...]

var dumpPorts = new HashSet<int> { 61556, 64090, 61555 };
var seconds = 0;
var outDir = ".";

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "-s" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
    else if (int.TryParse(args[i], out var p)) dumpPorts.Add(p);
    else outDir = args[i];
}

Directory.CreateDirectory(outDir);
var outFile = Path.Combine(outDir, $"loopback-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
using var writer = new StreamWriter(outFile) { AutoFlush = true };
void Both(string s) { Console.WriteLine(s); writer.WriteLine(s); }

Both($"hex-dump ports: {string.Join(",", dumpPorts.OrderBy(x => x))}  (ALL loopback TCP/UDP flows are counted)");
Both($"output: {outFile}");
Both(seconds > 0 ? $"stopping after {seconds}s" : "Ctrl+C to stop");

Socket sock;
try
{
    sock = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.IP);
    sock.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 0));
    sock.IOControl(IOControlCode.ReceiveAll, BitConverter.GetBytes(1), BitConverter.GetBytes(1));
    sock.ReceiveTimeout = 1000;
}
catch (SocketException ex)
{
    Both($"FAILED to open raw socket: {ex.Message} (needs an elevated/Administrator shell)");
    return 1;
}

var counts = new Dictionary<string, (long Packets, long Bytes)>();
var dumped = new Dictionary<string, int>();
var buffer = new byte[65535];
var deadline = DateTime.UtcNow.AddSeconds(seconds > 0 ? seconds : 86400);
long totalAll = 0;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; deadline = DateTime.UtcNow; };

while (DateTime.UtcNow < deadline)
{
    int n;
    try { n = sock.Receive(buffer); }
    catch (SocketException) { continue; }   // 1s timeout -> re-check deadline

    if (n < 24) continue;
    var ihl = (buffer[0] & 0x0F) * 4;
    if (ihl < 20 || n < ihl + 4) continue;

    var proto = buffer[9];
    string protoName;
    if (proto == 6) protoName = "TCP";
    else if (proto == 17) protoName = "UDP";
    else continue;

    var src = (buffer[ihl] << 8) | buffer[ihl + 1];
    var dst = (buffer[ihl + 2] << 8) | buffer[ihl + 3];

    int payloadOff, payloadLen;
    if (proto == 17)
    {
        var udpLen = (buffer[ihl + 4] << 8) | buffer[ihl + 5];
        payloadOff = ihl + 8;
        payloadLen = Math.Min(udpLen - 8, n - payloadOff);
    }
    else
    {
        if (n < ihl + 13) continue;
        var dataOff = (buffer[ihl + 12] >> 4) * 4;
        payloadOff = ihl + dataOff;
        payloadLen = n - payloadOff;
    }
    if (payloadLen < 0) payloadLen = 0;

    totalAll++;
    var key = $"{protoName} {src}->{dst}";
    counts.TryGetValue(key, out var prev);
    counts[key] = (prev.Packets + 1, prev.Bytes + payloadLen);

    if ((dumpPorts.Contains(src) || dumpPorts.Contains(dst)) && payloadLen > 0)
    {
        var dcount = dumped.TryGetValue(key, out var dc) ? dc : 0;
        if (dcount < 80)
        {
            dumped[key] = dcount + 1;
            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss.fff} {protoName} {src,5}->{dst,-5} len={payloadLen,-4} ");
            var show = Math.Min(payloadLen, 256);
            for (var i = 0; i < show; i++) sb.Append(buffer[payloadOff + i].ToString("x2"));
            if (payloadLen > show) sb.Append("...");
            writer.WriteLine(sb.ToString());
        }
    }

    if (totalAll % 500 == 0) Console.WriteLine($"  {totalAll} packets...");
}

Both("");
Both("=== all loopback flows seen (proto src->dst : packets, payloadBytes) ===");
foreach (var kv in counts.OrderByDescending(k => k.Value.Packets))
{
    Both($"{kv.Key} : {kv.Value.Packets} pkts, {kv.Value.Bytes} bytes");
}
Both($"total packets: {totalAll}");
return 0;
