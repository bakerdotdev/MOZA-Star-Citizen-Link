using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MozaStarCitizen.App.Diagnostics;
using MozaStarCitizen.App.Telemetry.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MozaStarCitizen.App.Telemetry;

/// <summary>
/// Derives Star Citizen force-feedback signals from the game's own audio.
///
/// Captures the system render endpoint with WASAPI loopback, downmixes to
/// mono, and runs <see cref="AudioTelemetryAnalyzer"/> to produce engine
/// rumble, atmosphere, and impact/weapon transients. It touches neither
/// StarCitizen.exe nor any D-BOX software, so there is no anti-cheat or
/// reverse-engineering exposure.
///
/// Audio cannot supply true motion vectors (G-force, attitude) or discrete
/// state (gear, decouple); those frame fields are left unset.
/// </summary>
public sealed class AudioDspTelemetrySource : IStarCitizenTelemetrySource
{
    private readonly AudioDspOptions _options;
    private readonly object _lifecycleLock = new();

    private IWaveIn? _capture;
    private MMDevice? _device;
    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;
    private int _currentPid;
    private AudioTelemetryAnalyzer? _analyzer;
    private Channel<StarCitizenTelemetryFrame> _channel =
        CreateChannel();
    private float[] _monoScratch = new float[4096];

    private string _deviceName = "(not selected)";
    private string _formatDescription = "(unknown)";
    private string _status = "Not initialized.";
    private bool _started;
    private long _windowsAnalyzed;
    private AudioFeatures _lastFeatures;
    private double _peakEngine;
    private double _peakAtmosphere;
    private double _peakImpact;
    private double _peakWeapon;
    private double _peakAfterburner;
    private double _peakEngineDb = double.NegativeInfinity;
    private double _peakAirDb = double.NegativeInfinity;

    public AudioDspTelemetrySource()
        : this(AudioDspOptions.FromEnvironment())
    {
    }

    public AudioDspTelemetrySource(AudioDspOptions options)
    {
        _options = options;
    }

    public string Name => "Star Citizen audio (DSP)";

    public string Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_started)
            {
                return Task.CompletedTask;
            }

            try
            {
                _channel = CreateChannel();
                ResetSessionStats();

                if (_options.CaptureProcessName is { Length: > 0 } processName)
                {
                    StartProcessLoopback(processName);
                }
                else
                {
                    StartDeviceLoopback();
                }

                _started = true;
                AppLog.Write(_status);
            }
            catch (Exception ex)
            {
                _status = $"Audio capture failed to start: {ex.Message}";
                AppLog.Write(ex, "Audio capture failed to start");
                StopCaptureCore();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    // Whole-device loopback (captures everything rendered to the device).
    private void StartDeviceLoopback()
    {
        _device = ResolveRenderDevice(_options.DeviceNameContains);
        _deviceName = _device.FriendlyName;

        var capture = new WasapiLoopbackCapture(_device);
        _formatDescription =
            $"{capture.WaveFormat.SampleRate} Hz, {capture.WaveFormat.Channels} ch, " +
            $"{capture.WaveFormat.BitsPerSample}-bit {capture.WaveFormat.Encoding}";

        _analyzer = new AudioTelemetryAnalyzer(capture.WaveFormat.SampleRate);
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;
        capture.StartRecording();
        _capture = capture;

        _status = $"Capturing loopback from \"{_deviceName}\" ({_formatDescription}); gain {_options.InputGain:0.##}x.";
    }

    // Per-process loopback: captures ONLY the target process's audio. A supervisor
    // watches for the process and (re)attaches as it starts/stops, so the app can
    // launch before or after the game.
    private void StartProcessLoopback(string processName)
    {
        _analyzer = new AudioTelemetryAnalyzer(48000);
        _deviceName = $"{processName}.exe (process loopback)";
        _formatDescription = "48000 Hz, 2 ch, 32-bit IeeeFloat";
        _status = $"Waiting for {processName} (process-loopback); gain {_options.InputGain:0.##}x.";

        _supervisorCts = new CancellationTokenSource();
        _supervisorTask = Task.Run(() => SuperviseProcessAsync(processName, _supervisorCts.Token));
    }

    private async Task SuperviseProcessAsync(string processName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var pid = Process.GetProcessesByName(processName).FirstOrDefault()?.Id ?? 0;
                if (pid != 0 && pid != _currentPid)
                {
                    SwapProcessCapture(pid, processName);
                }
                else if (pid == 0 && _currentPid != 0)
                {
                    StopActiveCapture();
                    _status = $"Waiting for {processName} (process-loopback).";
                    AppLog.Write(_status);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Process-loopback supervisor: {ex.Message}");
            }

            try { await Task.Delay(2000, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SwapProcessCapture(int pid, string processName)
    {
        StopActiveCapture();
        try
        {
            var capture = new ProcessLoopbackCapture(pid);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();
            _capture = capture;
            _currentPid = pid;
            _status = $"Capturing {processName}.exe (pid {pid}) process audio; gain {_options.InputGain:0.##}x.";
            AppLog.Write(_status);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"Process-loopback capture failed for {processName} (pid {pid})");
            _status = $"Process-loopback failed for {processName}: {ex.Message}";
        }
    }

    private void StopActiveCapture()
    {
        var capture = _capture;
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { /* ignore stop races */ }
            try { capture.Dispose(); } catch { /* ignore */ }
            _capture = null;
        }

        _currentPid = 0;
    }

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_started)
        {
            await InitializeAsync(cancellationToken);
        }

        var reader = _channel.Reader;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }
        finally
        {
            StopCapture();
        }
    }

    public Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var analyzer = _analyzer;
        var lines = new List<string>
        {
            $"Audio source: {Name}",
            $"Render device: {_deviceName}",
            $"Capture format: {_formatDescription}",
            $"Input gain: {_options.InputGain.ToString("0.##", CultureInfo.InvariantCulture)}x" +
                " (set MOZA_SC_AUDIO_GAIN to calibrate)",
            $"Status: {_status}",
            $"Windows analyzed: {_windowsAnalyzed}"
        };

        if (analyzer is not null)
        {
            lines.Add($"Analysis rate: {analyzer.FramesPerSecond:0.#} windows/sec");
            lines.Add(
                $"Levels: engine {FormatDb(analyzer.LastEngineDb)}, air {FormatDb(analyzer.LastAirDb)}; " +
                $"flux ratios impact {analyzer.LastImpactRatio:0.0}, weapon {analyzer.LastWeaponRatio:0.0}");
            lines.Add(
                $"Last signals: engine {_lastFeatures.EngineRumble:0.00} @ {_lastFeatures.EngineFrequencyHz:0} Hz, " +
                $"atmo {_lastFeatures.Atmosphere:0.00}, afterburner {_lastFeatures.Afterburner:0.00}, " +
                $"impact {_lastFeatures.Impact:0.00}, weapon {_lastFeatures.WeaponFire:0.00}");
        }

        lines.Add(
            $"Session peaks: engine {_peakEngine:0.00} ({FormatDb(_peakEngineDb)}), " +
            $"atmo {_peakAtmosphere:0.00} ({FormatDb(_peakAirDb)}), " +
            $"afterburner {_peakAfterburner:0.00}, impact {_peakImpact:0.00}, weapon {_peakWeapon:0.00}");
        lines.Add($"Calibration: {CalibrationHint()}");
        lines.Add("Note: audio cannot provide G-force, attitude, gear, or decouple state.");
        return Task.FromResult<IReadOnlyList<string>>(lines);
    }

    public ValueTask DisposeAsync()
    {
        StopCapture();
        return ValueTask.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var analyzer = _analyzer;
        var capture = _capture;
        if (analyzer is null || capture is null || e.BytesRecorded <= 0)
        {
            return;
        }

        var mono = Downmix(e.Buffer, e.BytesRecorded, capture.WaveFormat, _options.InputGain);
        if (mono.IsEmpty)
        {
            return;
        }

        analyzer.Add(mono, OnFeatures);
    }

    private void OnFeatures(AudioFeatures features)
    {
        _lastFeatures = features;
        _peakEngine = Math.Max(_peakEngine, features.EngineRumble);
        _peakAtmosphere = Math.Max(_peakAtmosphere, features.Atmosphere);
        _peakImpact = Math.Max(_peakImpact, features.Impact);
        _peakWeapon = Math.Max(_peakWeapon, features.WeaponFire);
        _peakAfterburner = Math.Max(_peakAfterburner, features.Afterburner);

        var stats = _analyzer;
        if (stats is not null)
        {
            if (double.IsFinite(stats.LastEngineDb))
            {
                _peakEngineDb = Math.Max(_peakEngineDb, stats.LastEngineDb);
            }

            if (double.IsFinite(stats.LastAirDb))
            {
                _peakAirDb = Math.Max(_peakAirDb, stats.LastAirDb);
            }
        }

        var window = Interlocked.Increment(ref _windowsAnalyzed);

        _channel.Writer.TryWrite(new StarCitizenTelemetryFrame
        {
            Source = Name,
            EngineRumble = features.EngineRumble,
            EngineFrequencyHz = features.EngineFrequencyHz,
            Atmosphere = features.Atmosphere,
            Boost = features.Boost,
            Impact = features.Impact,
            WeaponFire = features.WeaponFire,
            Afterburner = features.Afterburner
        });

        // Refresh the human-readable status roughly once per second without
        // touching it on every window.
        var analyzer = _analyzer;
        if (analyzer is not null && window % (long)Math.Max(1, analyzer.FramesPerSecond) == 0)
        {
            _status =
                $"Capturing \"{_deviceName}\": engine {features.EngineRumble:0.00} " +
                $"({FormatDb(analyzer.LastEngineDb)}), atmo {features.Atmosphere:0.00}, " +
                $"afterburner {features.Afterburner:0.00}.";
        }
    }

    private ReadOnlySpan<float> Downmix(byte[] buffer, int bytesRecorded, WaveFormat format, double gain)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var frames = bytesRecorded / (bytesPerSample * channels);
        if (frames <= 0)
        {
            return ReadOnlySpan<float>.Empty;
        }

        if (_monoScratch.Length < frames)
        {
            _monoScratch = new float[frames];
        }

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        var isPcm16 = format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16;

        if (isFloat)
        {
            var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
            for (var f = 0; f < frames; f++)
            {
                double sum = 0;
                var baseIndex = f * channels;
                for (var c = 0; c < channels; c++)
                {
                    sum += samples[baseIndex + c];
                }

                _monoScratch[f] = (float)Math.Clamp(sum / channels * gain, -4.0, 4.0);
            }
        }
        else if (isPcm16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRecorded));
            for (var f = 0; f < frames; f++)
            {
                double sum = 0;
                var baseIndex = f * channels;
                for (var c = 0; c < channels; c++)
                {
                    sum += samples[baseIndex + c] / 32768.0;
                }

                _monoScratch[f] = (float)Math.Clamp(sum / channels * gain, -4.0, 4.0);
            }
        }
        else
        {
            // Unsupported format; report once via status and produce nothing.
            _status = $"Unsupported capture format ({_formatDescription}); audio DSP idle.";
            return ReadOnlySpan<float>.Empty;
        }

        return _monoScratch.AsSpan(0, frames);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _status = $"Audio loopback capture stopped: {e.Exception.Message}";
            AppLog.Write(e.Exception, "Audio loopback capture stopped");
        }
    }

    private void StopCapture()
    {
        lock (_lifecycleLock)
        {
            StopCaptureCore();
        }
    }

    private void StopCaptureCore()
    {
        if (_supervisorCts is not null)
        {
            try { _supervisorCts.Cancel(); } catch { /* ignore */ }
            _supervisorCts.Dispose();
            _supervisorCts = null;
            _supervisorTask = null;
        }

        StopActiveCapture();

        if (_device is not null)
        {
            try { _device.Dispose(); } catch { /* ignore */ }
            _device = null;
        }

        _channel.Writer.TryComplete();
        _analyzer = null;
        _started = false;
    }

    private static MMDevice ResolveRenderDevice(string? nameContains)
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

            throw new InvalidOperationException(
                $"No active render device matching \"{nameContains}\" was found.");
        }

        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    private static Channel<StarCitizenTelemetryFrame> CreateChannel() =>
        Channel.CreateBounded<StarCitizenTelemetryFrame>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

    private void ResetSessionStats()
    {
        Interlocked.Exchange(ref _windowsAnalyzed, 0);
        _peakEngine = 0;
        _peakAtmosphere = 0;
        _peakImpact = 0;
        _peakWeapon = 0;
        _peakAfterburner = 0;
        _peakEngineDb = double.NegativeInfinity;
        _peakAirDb = double.NegativeInfinity;
    }

    private string CalibrationHint()
    {
        if (Interlocked.Read(ref _windowsAnalyzed) < 200)
        {
            return "not enough audio captured yet — fly with engine and weapons active for ~20s, then Refresh.";
        }

        if (_peakEngine >= 0.98)
        {
            return "engine peaked at full scale — lower MOZA_SC_AUDIO_GAIN (try 0.5) so idle isn't maxed out.";
        }

        if (_peakEngine < 0.30)
        {
            return "engine stayed weak — raise MOZA_SC_AUDIO_GAIN (try 2-4), or confirm this capture device carries the game audio.";
        }

        return "engine range looks usable; adjust MOZA_SC_AUDIO_GAIN to taste.";
    }

    private static string FormatDb(double db) =>
        double.IsFinite(db) ? $"{db:0.0} dB" : "-inf dB";
}
