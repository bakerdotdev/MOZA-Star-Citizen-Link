namespace MozaStarCitizen.App.Telemetry;

public static class TelemetrySourceFactory
{
    public static IStarCitizenTelemetrySource Create()
    {
        var mode = ParseMode(Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY"));
        var configuredUrl =
            Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY_URL") ??
            Environment.GetEnvironmentVariable("MOZA_SC_OFFICIAL_TELEMETRY_URL");

        return mode switch
        {
            TelemetrySourceMode.OfficialHttp when !string.IsNullOrWhiteSpace(configuredUrl) =>
                new HttpJsonTelemetrySource("Official HTTP telemetry", configuredUrl),
            TelemetrySourceMode.OfficialHttp =>
                new NoTelemetrySource(),
            TelemetrySourceMode.DBoxHaptiSync =>
                new DBoxHaptiSyncTelemetrySource(),
            TelemetrySourceMode.Preview =>
                new NoTelemetrySource(),
            _ when !string.IsNullOrWhiteSpace(configuredUrl) =>
                new HttpJsonTelemetrySource("Configured HTTP telemetry", configuredUrl),
            _ =>
                new DBoxHaptiSyncTelemetrySource()
        };
    }

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
    DBoxHaptiSync,
    OfficialHttp,
    Preview
}
