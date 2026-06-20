using System.Globalization;
using MozaStarCitizen.App.Telemetry.Audio;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

// AudioTune — record Star Citizen loopback audio and replay it through the
// exact production FFB analyzer, so the audio-DSP detection can be tuned
// against real game audio instead of guessed at.
//
//   record <out.wav> [seconds]         capture system loopback to WAV
//                                       (MOZA_SC_AUDIO_DEVICE picks a device)
//   replay <in.wav> [out.csv] [gain]   run the analyzer over a WAV and write a
//                                       per-window feature timeline (CSV)

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "list" => ListDevices(),
        "devices" => ListDevices(),
        "keytest" => KeyTest(),
        "spectrum" => Spectrum(args),
        "record" => Record(args),
        "record-proc" => RecordProcess(args),
        "replay" => Replay(args),
        _ => Fail(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}

static int Fail()
{
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("AudioTune — record/replay SC loopback audio through the FFB analyzer");
    Console.WriteLine();
    Console.WriteLine("  list                               list render (playback) devices for capture");
    Console.WriteLine("  keytest                            press keys to find one for MOZA_MARK_KEY");
    Console.WriteLine("  record <out.wav> [seconds]         capture system loopback to WAV (default 90s)");
    Console.WriteLine("                                     set MOZA_SC_AUDIO_DEVICE to pick a render device");
    Console.WriteLine("  record-proc <name|pid> <out.wav> [seconds]  capture ONE process's audio (per-process loopback)");
    Console.WriteLine("  replay <in.wav> [out.csv] [gain]   run analyzer over WAV -> per-window feature CSV");
    Console.WriteLine();
    Console.WriteLine("Workflow: record a flight with known events, then replay to see how each");
    Console.WriteLine("channel responds. Tweak AudioTelemetryAnalyzer, rebuild, replay again.");
}

// Average magnitude spectrum (in dB, by octave-ish band) over a time window —
// for locating where a signal's energy lives (e.g. atmospheric air rush).
static int Spectrum(string[] args)
{
    if (args.Length < 4)
    {
        throw new ArgumentException("spectrum <in.wav> <startSec> <endSec>");
    }

    var inPath = Path.GetFullPath(args[1]);
    var startSec = double.Parse(args[2], CultureInfo.InvariantCulture);
    var endSec = double.Parse(args[3], CultureInfo.InvariantCulture);

    using var reader = new AudioFileReader(inPath);
    var sr = reader.WaveFormat.SampleRate;
    var ch = Math.Max(1, reader.WaveFormat.Channels);

    var mono = new List<float>();
    var buf = new float[sr * ch];
    int read;
    while ((read = reader.Read(buf, 0, buf.Length)) > 0)
    {
        for (var f = 0; f < read / ch; f++)
        {
            double s = 0;
            for (var c = 0; c < ch; c++)
            {
                s += buf[(f * ch) + c];
            }

            mono.Add((float)(s / ch));
        }
    }

    const int N = 2048;
    const int order = 11;
    var win = new double[N];
    for (var i = 0; i < N; i++)
    {
        win[i] = FastFourierTransform.HannWindow(i, N);
    }

    var fft = new Complex[N];
    var acc = new double[(N / 2) + 1];
    var startS = Math.Max(0, (int)(startSec * sr));
    var endS = Math.Min(mono.Count, (int)(endSec * sr));
    var wins = 0;
    for (var pos = startS; pos + N <= endS; pos += N / 2)
    {
        for (var i = 0; i < N; i++)
        {
            fft[i].X = (float)(mono[pos + i] * win[i]);
            fft[i].Y = 0f;
        }

        FastFourierTransform.FFT(true, order, fft);
        for (var k = 0; k < acc.Length; k++)
        {
            acc[k] += Math.Sqrt((fft[k].X * fft[k].X) + (fft[k].Y * fft[k].Y));
        }

        wins++;
    }

    if (wins == 0)
    {
        Console.WriteLine($"No audio in [{startSec},{endSec}]s (file is {mono.Count / (double)sr:0.0}s).");
        return 0;
    }

    for (var k = 0; k < acc.Length; k++)
    {
        acc[k] /= wins;
    }

    var edges = new[] { 20, 60, 120, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    Console.WriteLine($"{Path.GetFileName(inPath)}  [{startSec:0.0}-{endSec:0.0}]s  ({wins} windows)");
    Console.WriteLine($"  {"band (Hz)",-14} {"avg dB",8}");
    for (var b = 0; b < edges.Length - 1; b++)
    {
        var lo = (int)Math.Round(edges[b] * (double)N / sr);
        var hi = (int)Math.Round(edges[b + 1] * (double)N / sr);
        double sum = 0;
        var n = 0;
        for (var k = Math.Max(1, lo); k <= Math.Min(acc.Length - 1, hi); k++)
        {
            sum += acc[k];
            n++;
        }

        var avg = n > 0 ? sum / n : 0;
        var db = 20.0 * Math.Log10(avg + 1e-9);
        Console.WriteLine($"  {edges[b] + "-" + edges[b + 1],-14} {db,8:0.0}");
    }

    return 0;
}

static int ListDevices()
{
    using var enumerator = new MMDeviceEnumerator();
    var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console).ID;

    Console.WriteLine("Render (playback) devices — capture loopback of any of these:");
    Console.WriteLine("(set MOZA_SC_AUDIO_DEVICE to a unique substring of the one carrying SC audio)");
    Console.WriteLine();
    foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        var marker = device.ID == defaultId ? " [default]" : "";
        Console.WriteLine($"  {device.FriendlyName}{marker}");
        device.Dispose();
    }

    return 0;
}

static int Record(string[] args)
{
    if (args.Length < 2)
    {
        throw new ArgumentException("record needs an output path: record <out.wav> [seconds]");
    }

    var outPath = Path.GetFullPath(args[1]);
    var seconds = args.Length > 2 && int.TryParse(args[2], out var s) && s > 0 ? s : 90;
    var deviceContains = Environment.GetEnvironmentVariable("MOZA_SC_AUDIO_DEVICE");

    using var device = ResolveRenderDevice(deviceContains);
    using var capture = new WasapiLoopbackCapture(device);

    Console.WriteLine($"Recording loopback from \"{device.FriendlyName}\"");
    Console.WriteLine($"Format: {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, " +
                      $"{capture.WaveFormat.BitsPerSample}-bit {capture.WaveFormat.Encoding}");
    Console.WriteLine($"Writing: {outPath}");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using var writer = new WaveFileWriter(outPath, capture.WaveFormat);

    var markerPath = Path.ChangeExtension(outPath, ".markers.csv");
    using var markers = new StreamWriter(markerPath) { AutoFlush = true };
    markers.WriteLine("t_sec,label");

    var stopped = new ManualResetEventSlim(false);
    capture.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);
    capture.RecordingStopped += (_, _) => stopped.Set();

    // Ground-truth event markers, read via GetAsyncKeyState so they register
    // while SC stays focused (no alt-tab). Keys are NOT swallowed, so pick one
    // SC ignores. The generic mark key is configurable via MOZA_MARK_KEY (e.g.
    // Insert, Home, End, PageUp, F9, Mouse4); with a scripted run, marks are
    // labelled by their order. Ctrl+Alt+1/2/3 = explicit weapon/boost/impact.
    var markVk = ParseMarkKey(Environment.GetEnvironmentVariable("MOZA_MARK_KEY"), out var markName);
    var hotkeys = new (int Vk, string Label)[] { (0x31, "weapon"), (0x32, "boost"), (0x33, "impact") };
    var wasDown = new bool[hotkeys.Length];
    var markWasDown = false;

    Console.WriteLine();
    Console.WriteLine("Stay in Star Citizen — markers are read globally, no need to tab out.");
    Console.WriteLine($"Tap {markName} at each event (labelled weapon->boost->impact by script order).");
    Console.WriteLine("   change it with MOZA_MARK_KEY (Insert, Home, End, PageUp, F9, Mouse4, ...)");
    Console.WriteLine("Or, if unbound in SC, Ctrl+Alt+1/2/3 = weapon/boost/impact explicitly.");
    Console.WriteLine($"Recording {seconds}s — Ctrl+C to stop early.");
    Console.WriteLine();

    Console.CancelKeyPress += (_, e) => { e.Cancel = true; capture.StopRecording(); };

    try
    {
        capture.StartRecording();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\nCould not capture \"{device.FriendlyName}\": {ex.Message}");
        Console.Error.WriteLine("That endpoint is busy — typically held in exclusive mode by Voicemeeter, an ASIO");
        Console.Error.WriteLine("host, or the interface's own console app. Options:");
        Console.Error.WriteLine("  • run 'list' and set MOZA_SC_AUDIO_DEVICE to a free device carrying SC audio");
        Console.Error.WriteLine("  • or uncheck 'Allow applications to take exclusive control' for it in Windows");
        Console.Error.WriteLine("    Sound > device Properties > Advanced.");
        return 3;
    }

    var start = DateTime.UtcNow;
    var lastShown = -1;
    while (!stopped.IsSet && (DateTime.UtcNow - start).TotalSeconds < seconds)
    {
        Thread.Sleep(15);
        var elapsed = (DateTime.UtcNow - start).TotalSeconds;

        void Mark(string label)
        {
            markers.WriteLine($"{elapsed.ToString("0.000", CultureInfo.InvariantCulture)},{label}");
            Console.WriteLine($"  [{elapsed,6:0.0}s] mark: {label}");
            lastShown = -1; // force the clock line to redraw next tick
        }

        var markDown = (Native.GetAsyncKeyState(markVk) & 0x8000) != 0;
        if (markDown && !markWasDown)
        {
            Mark("mark");
        }

        markWasDown = markDown;

        var ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
        var alt = (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
        for (var i = 0; i < hotkeys.Length; i++)
        {
            var down = ctrl && alt && (Native.GetAsyncKeyState(hotkeys[i].Vk) & 0x8000) != 0;
            if (down && !wasDown[i])
            {
                Mark(hotkeys[i].Label);
            }

            wasDown[i] = down;
        }

        var el = (int)elapsed;
        if (el != lastShown)
        {
            lastShown = el;
            Console.Write($"\r  {el,4}s / {seconds}s ");
        }
    }

    capture.StopRecording();
    stopped.Wait(2000);
    writer.Flush();
    Console.WriteLine($"\nDone. {writer.Length:n0} bytes -> {outPath}");
    Console.WriteLine($"Markers -> {markerPath}");
    return 0;
}

static int RecordProcess(string[] args)
{
    if (args.Length < 3)
    {
        throw new ArgumentException("record-proc <process-name|pid> <out.wav> [seconds]");
    }

    var target = args[1];
    var outPath = Path.GetFullPath(args[2]);
    var seconds = args.Length > 3 && int.TryParse(args[3], out var s) && s > 0 ? s : 30;

    if (!int.TryParse(target, out var pid))
    {
        var name = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target[..^4] : target;
        var proc = System.Diagnostics.Process.GetProcessesByName(name).FirstOrDefault()
            ?? throw new InvalidOperationException($"No running process named '{name}'.");
        pid = proc.Id;
        Console.WriteLine($"Target: {name} (pid {pid})");
    }
    else
    {
        Console.WriteLine($"Target pid {pid}");
    }

    using var capture = new ProcessLoopbackCapture(pid);
    Console.WriteLine($"Capture format: {capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, " +
                      $"{capture.WaveFormat.BitsPerSample}-bit {capture.WaveFormat.Encoding}");

    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    using var writer = new WaveFileWriter(outPath, capture.WaveFormat);

    Exception? captureError = null;
    var stopped = new ManualResetEventSlim(false);
    long bytes = 0;
    capture.DataAvailable += (_, e) => { writer.Write(e.Buffer, 0, e.BytesRecorded); Interlocked.Add(ref bytes, e.BytesRecorded); };
    capture.RecordingStopped += (_, e) => { captureError = e.Exception; stopped.Set(); };

    Console.WriteLine($"\nRecording ONLY this process's audio for {seconds}s — play audio in it now.\n");
    capture.StartRecording();
    var start = DateTime.UtcNow;
    while ((DateTime.UtcNow - start).TotalSeconds < seconds && !stopped.IsSet)
    {
        Thread.Sleep(500);
        Console.Write($"\r  {(int)(DateTime.UtcNow - start).TotalSeconds,3}s   {Interlocked.Read(ref bytes):n0} bytes ");
    }

    capture.StopRecording();
    stopped.Wait(1500);
    writer.Flush();
    Console.WriteLine($"\nDone. {writer.Length:n0} bytes -> {outPath}");
    if (captureError is not null)
    {
        Console.Error.WriteLine($"capture error: {captureError.Message}");
        return 2;
    }

    return 0;
}

static int Replay(string[] args)
{
    if (args.Length < 2)
    {
        throw new ArgumentException("replay needs an input WAV: replay <in.wav> [out.csv] [gain]");
    }

    var inPath = Path.GetFullPath(args[1]);
    if (!File.Exists(inPath))
    {
        throw new FileNotFoundException($"Input WAV not found: {inPath}");
    }

    var outCsv = Path.GetFullPath(args.Length > 2 ? args[2] : Path.ChangeExtension(inPath, ".features.csv"));
    var gain = args.Length > 3 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g > 0
        ? g
        : 1.0;

    using var reader = new AudioFileReader(inPath);
    var channels = Math.Max(1, reader.WaveFormat.Channels);
    var sampleRate = reader.WaveFormat.SampleRate;
    var analyzer = new AudioTelemetryAnalyzer(sampleRate);
    var fps = analyzer.FramesPerSecond;

    Console.WriteLine($"Replaying {inPath}");
    Console.WriteLine($"  {sampleRate} Hz, {channels} ch, gain {gain:0.##}x, {fps:0.#} windows/sec");

    Directory.CreateDirectory(Path.GetDirectoryName(outCsv)!);
    using var csv = new StreamWriter(outCsv);
    csv.WriteLine("t_sec,engineRumble,engineHz,atmosphere,boost,impact,weapon,engineDb,airDb,impactRatio,weaponRatio,airFlatness,afterburner");

    var block = new float[sampleRate * channels];          // ~1s of interleaved samples
    var mono = new float[sampleRate + 1];
    long windowIndex = 0;
    double peakEngine = 0, peakAtmo = 0, peakImpact = 0, peakWeapon = 0, peakBoost = 0;
    int impactHits = 0, weaponHits = 0;

    int read;
    while ((read = reader.Read(block, 0, block.Length)) > 0)
    {
        var frames = read / channels;
        for (var f = 0; f < frames; f++)
        {
            double sum = 0;
            var baseIndex = f * channels;
            for (var c = 0; c < channels; c++)
            {
                sum += block[baseIndex + c];
            }

            mono[f] = (float)Math.Clamp(sum / channels * gain, -4.0, 4.0);
        }

        analyzer.Add(mono.AsSpan(0, frames), feat =>
        {
            var t = windowIndex / fps;
            windowIndex++;

            peakEngine = Math.Max(peakEngine, feat.EngineRumble);
            peakAtmo = Math.Max(peakAtmo, feat.Atmosphere);
            peakImpact = Math.Max(peakImpact, feat.Impact);
            peakWeapon = Math.Max(peakWeapon, feat.WeaponFire);
            peakBoost = Math.Max(peakBoost, feat.Boost);
            if (feat.Impact > 0.2) impactHits++;
            if (feat.WeaponFire > 0.2) weaponHits++;

            csv.WriteLine(string.Join(",",
                t.ToString("0.000", CultureInfo.InvariantCulture),
                feat.EngineRumble.ToString("0.0000", CultureInfo.InvariantCulture),
                feat.EngineFrequencyHz.ToString("0.0", CultureInfo.InvariantCulture),
                feat.Atmosphere.ToString("0.0000", CultureInfo.InvariantCulture),
                feat.Boost.ToString("0.0000", CultureInfo.InvariantCulture),
                feat.Impact.ToString("0.0000", CultureInfo.InvariantCulture),
                feat.WeaponFire.ToString("0.0000", CultureInfo.InvariantCulture),
                Db(analyzer.LastEngineDb),
                Db(analyzer.LastAirDb),
                analyzer.LastImpactRatio.ToString("0.00", CultureInfo.InvariantCulture),
                analyzer.LastWeaponRatio.ToString("0.00", CultureInfo.InvariantCulture),
                analyzer.LastAirFlatness.ToString("0.000", CultureInfo.InvariantCulture),
                feat.Afterburner.ToString("0.0000", CultureInfo.InvariantCulture)));
        });
    }

    csv.Flush();
    Console.WriteLine($"  {windowIndex} windows ({windowIndex / fps:0.0}s) -> {outCsv}");
    Console.WriteLine($"  peaks: engine {peakEngine:0.00}, atmo {peakAtmo:0.00}, boost {peakBoost:0.00}, " +
                      $"impact {peakImpact:0.00}, weapon {peakWeapon:0.00}");
    Console.WriteLine($"  windows over 0.2: impact {impactHits}, weapon {weaponHits}");
    return 0;
}

static MMDevice ResolveRenderDevice(string? nameContains)
{
    using var enumerator = new MMDeviceEnumerator();
    if (!string.IsNullOrWhiteSpace(nameContains))
    {
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (device.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }

            device.Dispose();
        }

        throw new InvalidOperationException($"No active render device matching \"{nameContains}\".");
    }

    return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
}

static string Db(double db) =>
    double.IsFinite(db) ? db.ToString("0.0", CultureInfo.InvariantCulture) : "-inf";

static int ParseMarkKey(string? raw, out string name)
{
    raw = raw?.Trim();
    if (!string.IsNullOrEmpty(raw))
    {
        if (Keys.Map.TryGetValue(raw, out var k))
        {
            name = k.Name;
            return k.Vk;
        }

        if ((raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
             int.TryParse(raw.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vkHex)))
        {
            name = $"VK 0x{vkHex:X2}";
            return vkHex;
        }

        Console.Error.WriteLine($"Unknown MOZA_MARK_KEY '{raw}'; using ScrollLock. Try: {string.Join(", ", Keys.Map.Values.Select(v => v.Name).Distinct())}");
    }

    name = "ScrollLock";
    return 0x91;
}

// Live key probe: press candidate keys to see which fire (and confirm in SC
// they do nothing) without guessing virtual-key codes.
static int KeyTest()
{
    Console.WriteLine("Key test — press keys to see them register globally (Ctrl+C to quit).");
    Console.WriteLine("Find one that fires here AND does nothing in SC, then use MOZA_MARK_KEY=<name>.");
    Console.WriteLine();
    var probes = Keys.Map.Values.Distinct().ToArray();
    var down = new bool[probes.Length];
    var run = true;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; run = false; };
    while (run)
    {
        Thread.Sleep(15);
        for (var i = 0; i < probes.Length; i++)
        {
            var d = (Native.GetAsyncKeyState(probes[i].Vk) & 0x8000) != 0;
            if (d && !down[i])
            {
                Console.WriteLine($"  pressed: {probes[i].Name}  (MOZA_MARK_KEY={probes[i].Name})");
            }

            down[i] = d;
        }
    }

    return 0;
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);
}

// Named marker keys — commonly present and usually unbound in SC's flight
// context. ParseMarkKey also accepts raw "0x2D" hex; default is ScrollLock.
static class Keys
{
    public static readonly Dictionary<string, (int Vk, string Name)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["insert"] = (0x2D, "Insert"), ["ins"] = (0x2D, "Insert"),
        ["delete"] = (0x2E, "Delete"), ["del"] = (0x2E, "Delete"),
        ["home"] = (0x24, "Home"), ["end"] = (0x23, "End"),
        ["pageup"] = (0x21, "PageUp"), ["pgup"] = (0x21, "PageUp"),
        ["pagedown"] = (0x22, "PageDown"), ["pgdn"] = (0x22, "PageDown"),
        ["pause"] = (0x13, "Pause"), ["scrolllock"] = (0x91, "ScrollLock"),
        ["numlock"] = (0x90, "NumLock"), ["apps"] = (0x5D, "Apps"), ["menu"] = (0x5D, "Apps"),
        ["mouse4"] = (0x05, "Mouse4"), ["mouse5"] = (0x06, "Mouse5"),
        ["f6"] = (0x75, "F6"), ["f7"] = (0x76, "F7"), ["f8"] = (0x77, "F8"),
        ["f9"] = (0x78, "F9"), ["f10"] = (0x79, "F10"), ["f11"] = (0x7A, "F11"), ["f12"] = (0x7B, "F12"),
    };
}
