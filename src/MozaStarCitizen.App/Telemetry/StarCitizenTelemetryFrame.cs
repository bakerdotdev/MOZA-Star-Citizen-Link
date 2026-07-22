using System.Text.Json.Serialization;

namespace MozaStarCitizen.App.Telemetry;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TelemetrySourceKind
{
    Unknown,
    DBoxSdkSample
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TelemetrySignalSet
{
    None = 0,
    EngineRumble = 1 << 0,
    EngineFrequency = 1 << 1,
    GForce = 1 << 2,
    Boost = 1 << 3,
    Impact = 1 << 4,
    LandingGear = 1 << 5
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TelemetryFrameBoundary
{
    None,
    Stop,
    ResetState,
    Close,
    Terminate,
    ReplayComplete
}

public sealed record StarCitizenTelemetryFrame
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Source { get; init; } = "Unknown";

    public TelemetrySourceKind SourceKind { get; init; }

    public string? ApplicationKey { get; init; }

    public TelemetrySignalSet UpdatedSignals { get; init; }

    public TelemetryFrameBoundary Boundary { get; init; }

    public double EngineRumble { get; init; }

    public double EngineFrequencyHz { get; init; }

    public double Atmosphere { get; init; }

    public double GForceLongitudinal { get; init; }

    public double GForceLateral { get; init; }

    public double GForceVertical { get; init; }

    public double Boost { get; init; }

    public double Afterburner { get; init; }

    public double Impact { get; init; }

    public double WeaponFire { get; init; }

    public double Countermeasure { get; init; }

    public double LandingGear { get; init; }

    public bool? Decoupled { get; init; }

    public string? RawKind { get; init; }

    public bool HasAnySignal =>
        EngineRumble > 0 ||
        EngineFrequencyHz > 0 ||
        Atmosphere > 0 ||
        GForceLongitudinal != 0 ||
        GForceLateral != 0 ||
        GForceVertical != 0 ||
        Boost > 0 ||
        Afterburner > 0 ||
        Impact > 0 ||
        WeaponFire > 0 ||
        Countermeasure > 0 ||
        LandingGear > 0 ||
        Decoupled is not null;
}
