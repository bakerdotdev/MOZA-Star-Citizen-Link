using MozaStarCitizen.App.Models;
using MozaStarCitizen.App.Telemetry;

namespace MozaStarCitizen.App.ForceFeedback;

public sealed class ForceFeedbackController
{
    private static readonly TimeSpan MinimumStateUpdateInterval = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DuplicateImpactWindow = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan DuplicateWeaponWindow = TimeSpan.FromMilliseconds(85);
    private static readonly TimeSpan DuplicateMechanicalWindow = TimeSpan.FromMilliseconds(250);
    private readonly IForceFeedbackDevice _device;
    private readonly Dictionary<string, StateEffectSnapshot> _states = [];
    private DateTimeOffset _lastImpact = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWeaponFire = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMechanicalPulse = DateTimeOffset.MinValue;
    private DateTimeOffset _lastBoostPulse = DateTimeOffset.MinValue;
    private bool? _lastDecoupled;
    private double _lastBoostSignal;

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
        var updates = new List<string>();

        var engineIntensity = Clamp01(frame.EngineRumble + (frame.Boost * 0.2) + (frame.Afterburner * 0.3));
        await UpdateStateAsync(
            "engine-rumble",
            engineIntensity > 0.03,
            new ForceEffect(
                ForceEffectKind.StateVibration,
                "Telemetry engine rumble",
                QuantizeIntensity(engineIntensity),
                TimeSpan.Zero,
                QuantizeFrequency(frame.EngineFrequencyHz > 0 ? frame.EngineFrequencyHz : 18 + engineIntensity * 34),
                "engine-rumble"),
            frame.Timestamp,
            updates,
            cancellationToken);

        var atmosphereIntensity = Clamp01(frame.Atmosphere * 0.35);
        await UpdateStateAsync(
            "atmosphere",
            atmosphereIntensity > 0.025,
            new ForceEffect(
                ForceEffectKind.StateVibration,
                "Telemetry atmosphere texture",
                QuantizeIntensity(Math.Max(0.08, atmosphereIntensity)),
                TimeSpan.Zero,
                QuantizeFrequency(14 + atmosphereIntensity * 22),
                "atmosphere"),
            frame.Timestamp,
            updates,
            cancellationToken);

        var gLoadIntensity = GetGLoadIntensity(frame);
        await UpdateStateAsync(
            "g-load",
            gLoadIntensity > 0.04,
            new ForceEffect(
                ForceEffectKind.StateVibration,
                "Telemetry G-load pressure",
                QuantizeIntensity(gLoadIntensity),
                TimeSpan.Zero,
                QuantizeFrequency(9 + gLoadIntensity * 12),
                "g-load"),
            frame.Timestamp,
            updates,
            cancellationToken);

        // Afterburner engaging gives one soft kick (the sustained surge comes
        // through the engine-rumble intensity above). Gentle so it's a swell, not
        // a collision-grade jolt.
        var boostIntensity = Clamp01(Math.Max(frame.Boost, frame.Afterburner));
        if (boostIntensity > 0.35 &&
            _lastBoostSignal <= 0.35 &&
            frame.Timestamp - _lastBoostPulse >= TimeSpan.FromMilliseconds(500))
        {
            await _device.PlayAsync(new ForceEffect(
                ForceEffectKind.Bump,
                "Telemetry boost kick",
                QuantizeIntensity(0.2 + boostIntensity * 0.2),
                TimeSpan.FromMilliseconds(160),
                0,
                null), cancellationToken);
            _lastBoostPulse = frame.Timestamp;
            updates.Add("boost kick");
        }

        _lastBoostSignal = boostIntensity;

        // Impact = collisions only. High trigger so weapon/afterburner transients
        // (small impact cross-talk) don't jolt; intensity scales from the hit so
        // real collisions stay punchy.
        if (frame.Impact > 0.35 && frame.Timestamp - _lastImpact >= DuplicateImpactWindow)
        {
            await _device.PlayAsync(new ForceEffect(
                ForceEffectKind.Bump,
                "Telemetry impact",
                QuantizeIntensity(Math.Max(0.3, frame.Impact)),
                TimeSpan.FromMilliseconds(220),
                0,
                null), cancellationToken);
            _lastImpact = frame.Timestamp;
            updates.Add("impact");
        }

        if (frame.WeaponFire > 0.05 && frame.Timestamp - _lastWeaponFire >= DuplicateWeaponWindow)
        {
            // A distinct recoil pop, clearly above the sustained engine rumble.
            await _device.PlayAsync(new ForceEffect(
                ForceEffectKind.Bump,
                "Telemetry weapon recoil",
                QuantizeIntensity(0.28 + frame.WeaponFire * 0.5),
                TimeSpan.FromMilliseconds(110),
                0,
                null), cancellationToken);
            _lastWeaponFire = frame.Timestamp;
            updates.Add("weapon recoil");
        }

        var mechanicalPulse = Math.Max(frame.LandingGear, frame.Countermeasure);
        if (mechanicalPulse > 0.05 && frame.Timestamp - _lastMechanicalPulse >= DuplicateMechanicalWindow)
        {
            await _device.PlayAsync(new ForceEffect(
                ForceEffectKind.PeriodicVibration,
                frame.LandingGear >= frame.Countermeasure ? "Telemetry landing gear motion" : "Telemetry countermeasure launch",
                QuantizeIntensity(0.18 + mechanicalPulse * 0.4),
                TimeSpan.FromMilliseconds(180),
                38,
                null), cancellationToken);
            _lastMechanicalPulse = frame.Timestamp;
            updates.Add(frame.LandingGear >= frame.Countermeasure ? "gear motion" : "countermeasure");
        }

        if (frame.Decoupled is { } decoupled)
        {
            if (_lastDecoupled is not null && _lastDecoupled.Value != decoupled)
            {
                await _device.PlayAsync(new ForceEffect(
                    ForceEffectKind.Bump,
                    decoupled ? "Telemetry decouple engaged" : "Telemetry coupled engaged",
                    decoupled ? 0.32 : 0.24,
                    TimeSpan.FromMilliseconds(120),
                    0,
                    null), cancellationToken);
                updates.Add(decoupled ? "decouple" : "couple");
            }

            _lastDecoupled = decoupled;
        }

        return updates.Count == 0
            ? "Telemetry frame received; no force update needed."
            : $"Telemetry force update: {string.Join(", ", updates)}.";
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        _states.Clear();
        _lastDecoupled = null;
        _lastBoostSignal = 0;
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

    private static double GetGLoadIntensity(StarCitizenTelemetryFrame frame)
    {
        var magnitude = Math.Sqrt(
            frame.GForceLateral * frame.GForceLateral +
            frame.GForceLongitudinal * frame.GForceLongitudinal +
            frame.GForceVertical * frame.GForceVertical);

        if (magnitude <= 0)
        {
            return 0;
        }

        return magnitude <= 1
            ? Clamp01(magnitude)
            : Clamp01((magnitude - 1) / 4);
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
            "Telemetry engine rumble",
            0.25,
            TimeSpan.Zero,
            26,
            "engine-rumble"),
        new ForceEffect(
            ForceEffectKind.Bump,
            "Telemetry impact",
            0.65,
            TimeSpan.FromMilliseconds(220),
            0,
            null),
        new ForceEffect(
            ForceEffectKind.StateVibration,
            "Telemetry atmosphere texture",
            0.12,
            TimeSpan.Zero,
            18,
            "atmosphere"),
        new ForceEffect(
            ForceEffectKind.StateVibration,
            "Telemetry G-load pressure",
            0.18,
            TimeSpan.Zero,
            12,
            "g-load"),
        new ForceEffect(
            ForceEffectKind.Bump,
            "Telemetry boost kick",
            0.55,
            TimeSpan.FromMilliseconds(180),
            0,
            null)
    ];

    private sealed record StateEffectSnapshot(double Intensity, double FrequencyHz, DateTimeOffset UpdatedAt);
}
