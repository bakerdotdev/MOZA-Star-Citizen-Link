namespace MozaStarCitizen.App.Models;

public enum ForceEffectKind
{
    PeriodicVibration,
    Bump,
    StateVibration,
    ConstantForce
}

public sealed record ForceEffect(
    ForceEffectKind Kind,
    string Name,
    double Intensity,
    TimeSpan Duration,
    double FrequencyHz,
    string? StateKey);
