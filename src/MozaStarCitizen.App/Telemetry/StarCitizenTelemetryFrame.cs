namespace MozaStarCitizen.App.Telemetry;

public sealed record StarCitizenTelemetryFrame
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Source { get; init; } = "Unknown";

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
