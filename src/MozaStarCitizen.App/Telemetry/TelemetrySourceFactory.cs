using System.Globalization;
using MozaStarCitizen.App.Telemetry.DBoxSdkLog;

namespace MozaStarCitizen.App.Telemetry;

public static class TelemetrySourceFactory
{
    public static IStarCitizenTelemetrySource Create()
    {
        var mode = ParseMode(Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY"));
        var configuredUrl =
            Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY_URL") ??
            Environment.GetEnvironmentVariable("MOZA_SC_OFFICIAL_TELEMETRY_URL");
        var configuredDBoxLog = FirstNonBlank(
            Environment.GetEnvironmentVariable("MOZA_SC_DBOX_XML_LOG"),
            Environment.GetEnvironmentVariable("MOZA_SC_DBOX_LOG"));

        IStarCitizenTelemetrySource source = mode switch
        {
            TelemetrySourceMode.OfficialHttp when !string.IsNullOrWhiteSpace(configuredUrl) =>
                new HttpJsonTelemetrySource("Official HTTP telemetry", configuredUrl),
            TelemetrySourceMode.OfficialHttp =>
                new NoTelemetrySource(TelemetryOutputPolicy.EffectsAllowed),
            TelemetrySourceMode.DBoxHaptiSync =>
                new DBoxHaptiSyncTelemetrySource(),
            TelemetrySourceMode.DBoxSdkSampleLog when !string.IsNullOrWhiteSpace(configuredDBoxLog) =>
                new DBoxSdkSampleLogTelemetrySource(
                    configuredDBoxLog,
                    ParseReplaySpeed(Environment.GetEnvironmentVariable("MOZA_SC_DBOX_REPLAY_SPEED"))),
            TelemetrySourceMode.DBoxSdkSampleLog =>
                new NoTelemetrySource(TelemetryOutputPolicy.VisualizationOnly),
            TelemetrySourceMode.AudioDsp =>
                new AudioDspTelemetrySource(),
            TelemetrySourceMode.Preview =>
                new NoTelemetrySource(TelemetryOutputPolicy.VisualizationOnly),
            _ when !string.IsNullOrWhiteSpace(configuredUrl) =>
                new HttpJsonTelemetrySource("Configured HTTP telemetry", configuredUrl),
            _ =>
                new AudioDspTelemetrySource()
        };

        // Audio path: gate the result on flight context (so menu music can't drive
        // phantom feedback when not flying) and on window focus (so FFB only fires
        // while you're actually looking at SC).
        if (source is AudioDspTelemetrySource && ContextGateEnabled())
        {
            var foreground = FocusGateEnabled() ? new ForegroundWatcher() : null;
            source = new ContextGatedTelemetrySource(source, new GameLogContextWatcher(), foreground, EngineGateEnabled());
        }

        return source;
    }

    private static bool ContextGateEnabled() =>
        !IsOff(Environment.GetEnvironmentVariable("MOZA_SC_CONTEXT_GATE"));

    private static bool FocusGateEnabled() =>
        !IsOff(Environment.GetEnvironmentVariable("MOZA_SC_FOCUS_GATE"));

    private static bool EngineGateEnabled() =>
        !IsOff(Environment.GetEnvironmentVariable("MOZA_SC_ENGINE_GATE"));

    private static bool IsOff(string? value) =>
        string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);

    private static double ParseReplaySpeed(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) &&
        double.IsFinite(speed) &&
        speed is >= 0 and <= 100
            ? speed
            : 1;

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static TelemetrySourceMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return TelemetrySourceMode.Auto;
        }

        return Enum.TryParse<TelemetrySourceMode>(value, ignoreCase: true, out var mode)
            ? mode
            : TelemetrySourceMode.Auto;
    }
}

public enum TelemetrySourceMode
{
    Auto,
    AudioDsp,
    DBoxHaptiSync,
    DBoxSdkSampleLog,
    OfficialHttp,
    Preview
}
