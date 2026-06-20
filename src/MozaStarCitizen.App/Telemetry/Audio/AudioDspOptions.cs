using System.Globalization;

namespace MozaStarCitizen.App.Telemetry.Audio;

/// <summary>
/// Configuration for the audio-DSP telemetry path. Defaults are tuned for
/// Star Citizen audio at a normal mix level; the gain knob lets a user
/// calibrate without rebuilding.
/// </summary>
public sealed class AudioDspOptions
{
    /// <summary>Linear multiplier applied to captured samples before analysis.</summary>
    public double InputGain { get; init; } = 1.0;

    /// <summary>
    /// Optional case-insensitive substring used to pick a specific render
    /// (playback) device for loopback. When null/empty the default render
    /// device is captured.
    /// </summary>
    public string? DeviceNameContains { get; init; }

    /// <summary>
    /// Process to capture via WASAPI per-process loopback (audio from only that
    /// process tree — so music/browser/Discord never reach the analyzer). When
    /// null, falls back to whole-device loopback (<see cref="DeviceNameContains"/>).
    /// Controlled by MOZA_SC_AUDIO_SOURCE: unset/"process" =&gt; "StarCitizen";
    /// "device" =&gt; device loopback; any other value =&gt; that process name.
    /// </summary>
    public string? CaptureProcessName { get; init; } = "StarCitizen";

    public static AudioDspOptions FromEnvironment()
    {
        return new AudioDspOptions
        {
            InputGain = ReadGain(),
            DeviceNameContains = Trim(Environment.GetEnvironmentVariable("MOZA_SC_AUDIO_DEVICE")),
            CaptureProcessName = ReadCaptureProcess()
        };
    }

    private static string? ReadCaptureProcess()
    {
        var raw = Trim(Environment.GetEnvironmentVariable("MOZA_SC_AUDIO_SOURCE"));
        return raw?.ToLowerInvariant() switch
        {
            null or "process" or "auto" or "starcitizen" => "StarCitizen",
            "device" or "loopback" => null,
            _ => raw!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw
        };
    }

    private static double ReadGain()
    {
        var raw = Environment.GetEnvironmentVariable("MOZA_SC_AUDIO_GAIN");
        if (!string.IsNullOrWhiteSpace(raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value > 0)
        {
            return Math.Clamp(value, 0.05, 64);
        }

        return 1.0;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
