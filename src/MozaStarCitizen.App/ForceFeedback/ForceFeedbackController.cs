using MozaStarCitizen.App.Models;
using MozaStarCitizen.App.Telemetry;

namespace MozaStarCitizen.App.ForceFeedback;

/// <summary>
/// Maps audio-derived telemetry to force feedback. Deliberately minimal: only two
/// sustained rumbles are produced —
/// <list type="bullet">
/// <item><b>Afterburner rumble</b>: a deep surge while the engine is held at
/// sustained high output. Normal-throttle flight in space stays silent by design;
/// you only feel it when you afterburner.</item>
/// <item><b>Atmospheric rumble</b>: a lighter buffeting texture from the air-rush
/// band during atmospheric flight.</item>
/// </list>
/// All transient "jolt" effects (impact, weapon recoil, boost kick, gear,
/// countermeasure, decouple) and the always-on base engine rumble were removed:
/// audio can't disambiguate them reliably, and in testing they misfired more than
/// they added.
/// </summary>
public sealed class ForceFeedbackController
{
    private static readonly TimeSpan MinimumStateUpdateInterval = TimeSpan.FromMilliseconds(180);
    private readonly IForceFeedbackDevice _device;
    private readonly Dictionary<string, StateEffectSnapshot> _states = [];

    public ForceFeedbackController(IForceFeedbackDevice device)
    {
        _device = device;
    }

    public string OutputName => _device.Name;

    public string OutputStatus => _device.Status;

    public IForceFeedbackDevice Device => _device;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _device.InitializeAsync(cancellationToken);
        await _device.PrepareAsync(GetTelemetryEffects(), cancellationToken);
    }

    public async Task<string> HandleTelemetryAsync(StarCitizenTelemetryFrame frame, CancellationToken cancellationToken)
    {
        if (frame.SourceKind == TelemetrySourceKind.DBoxSdkSample)
        {
            return "D-BOX SDK sample frame blocked by the visualization-only output policy.";
        }

        var updates = new List<string>();

        // Afterburner rumble: a deep rumble that swells in only while the engine is
        // held near its top (the analyzer's sustained-high detector). Flying at
        // normal throttle in space produces no afterburner signal -> no rumble.
        var afterburnerIntensity = Clamp01(frame.Afterburner * 0.7);
        await UpdateStateAsync(
            "afterburner-rumble",
            afterburnerIntensity > 0.05,
            new ForceEffect(
                ForceEffectKind.StateVibration,
                "Telemetry afterburner rumble",
                QuantizeIntensity(afterburnerIntensity),
                TimeSpan.Zero,
                30,
                "afterburner-rumble"),
            frame.Timestamp,
            updates,
            cancellationToken);

        // Atmospheric rumble from the air-rush band (tonal-wind branch of the air
        // detector). The signal reads ~0.4 in real atmospheric flight, so a modest
        // boost gives a present-but-light texture. The shouldRun threshold keeps
        // the small cruise leak from firing a buzz.
        var atmosphereIntensity = Clamp01(frame.Atmosphere * 1.3);
        await UpdateStateAsync(
            "atmosphere",
            atmosphereIntensity > 0.15,
            new ForceEffect(
                ForceEffectKind.StateVibration,
                "Telemetry atmosphere texture",
                QuantizeIntensity(atmosphereIntensity),
                TimeSpan.Zero,
                QuantizeFrequency(16 + atmosphereIntensity * 20),
                "atmosphere"),
            frame.Timestamp,
            updates,
            cancellationToken);

        return updates.Count == 0
            ? "Telemetry frame received; no force update needed."
            : $"Telemetry force update: {string.Join(", ", updates)}.";
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        _states.Clear();
        await _device.StopAllAsync(cancellationToken);
    }

    private async Task UpdateStateAsync(
        string stateKey,
        bool shouldRun,
        ForceEffect effect,
        DateTimeOffset timestamp,
        ICollection<string> updates,
        CancellationToken cancellationToken)
    {
        if (!shouldRun)
        {
            if (_states.Remove(stateKey))
            {
                await _device.StopAsync(stateKey, cancellationToken);
                updates.Add($"{stateKey} stopped");
            }

            return;
        }

        if (_states.TryGetValue(stateKey, out var previous) &&
            timestamp - previous.UpdatedAt < MinimumStateUpdateInterval &&
            Math.Abs(previous.Intensity - effect.Intensity) < 0.08 &&
            Math.Abs(previous.FrequencyHz - effect.FrequencyHz) < 2)
        {
            return;
        }

        await _device.PlayAsync(effect, cancellationToken);
        _states[stateKey] = new StateEffectSnapshot(effect.Intensity, effect.FrequencyHz, timestamp);
        updates.Add(stateKey);
    }

    private static double Clamp01(double value) =>
        Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);

    private static double QuantizeIntensity(double value) =>
        Math.Round(Clamp01(value) / 0.05, MidpointRounding.AwayFromZero) * 0.05;

    private static double QuantizeFrequency(double value) =>
        Math.Round(Math.Clamp(double.IsFinite(value) ? value : 20, 1, 90), MidpointRounding.AwayFromZero);

    private static IReadOnlyList<ForceEffect> GetTelemetryEffects() =>
    [
        new ForceEffect(
            ForceEffectKind.StateVibration,
            "Telemetry afterburner rumble",
            0.5,
            TimeSpan.Zero,
            30,
            "afterburner-rumble"),
        new ForceEffect(
            ForceEffectKind.StateVibration,
            "Telemetry atmosphere texture",
            0.12,
            TimeSpan.Zero,
            18,
            "atmosphere")
    ];

    private sealed record StateEffectSnapshot(double Intensity, double FrequencyHz, DateTimeOffset UpdatedAt);
}
