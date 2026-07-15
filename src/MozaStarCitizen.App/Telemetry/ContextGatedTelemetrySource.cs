using System.Runtime.CompilerServices;

namespace MozaStarCitizen.App.Telemetry;

/// <summary>
/// Wraps a telemetry source with gating: frames pass through only when
/// <list type="bullet">
/// <item>the player is in an active flight context (<see cref="GameLogContextWatcher"/>),</item>
/// <item>optionally, Star Citizen is the focused window (<see cref="ForegroundWatcher"/>), and</item>
/// <item>optionally, a sustained engine drone is present (you're in a powered ship,
/// not on foot) — judged from a slow average of engine rumble so a burst of FPS
/// gunfire or ambient noise doesn't count as "in a ship".</item>
/// </list>
/// Otherwise a zeroed frame is emitted so the force-feedback controller stops all
/// effects — killing phantom jolts from menu music, alt-tabbed audio, or FPS
/// gunfire while walking around outside the ship.
/// </summary>
public sealed class ContextGatedTelemetrySource : IStarCitizenTelemetrySource
{
    // Slow average of engine rumble (~1 s time constant at the audio frame rate),
    // so only a SUSTAINED engine drone — not a transient low-frequency spike from
    // gunfire/footsteps — keeps the gate open.
    // Engine-presence gate: suppress feedback when there's no sustained engine
    // drone (on foot / in a hangar). The threshold is deliberately LOW with
    // hysteresis — a high absolute level can't work because quieter ships never
    // clear it (the gate would mute them entirely), and a tight threshold
    // flickers mid-flight as the engine dips, cutting the rumble out.
    private const double EnginePresenceRate = 0.03;
    private const double EngineOnThreshold = 0.08;
    private const double EngineOffThreshold = 0.03;

    private readonly IStarCitizenTelemetrySource _inner;
    private readonly GameLogContextWatcher _context;
    private readonly ForegroundWatcher? _foreground;
    private readonly bool _engineGate;
    private double _enginePresence;
    private bool _enginePresent;

    public ContextGatedTelemetrySource(
        IStarCitizenTelemetrySource inner,
        GameLogContextWatcher context,
        ForegroundWatcher? foreground = null,
        bool engineGate = true)
    {
        _inner = inner;
        _context = context;
        _foreground = foreground;
        _engineGate = engineGate;
    }

    public string Name => _inner.Name;

    public string Status => Active
        ? _inner.Status
        : $"[gated: {GateReason}] {_inner.Status}";

    private bool EnginePresent => !_engineGate || _enginePresent;

    private bool Active => _context.InFlight && (_foreground?.IsFocused ?? true) && EnginePresent;

    private string GateReason =>
        !_context.InFlight ? "not in flight"
        : !(_foreground?.IsFocused ?? true) ? "SC not focused"
        : "no engine (on foot?)";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _context.Start();
        _foreground?.Start();
        await _inner.InitializeAsync(cancellationToken);
    }

    public async IAsyncEnumerable<StarCitizenTelemetryFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _inner.ReadFramesAsync(cancellationToken))
        {
            // Track engine presence from the RAW frame (before gating) so the gate
            // can re-open when the engine drone returns.
            if (_engineGate)
            {
                _enginePresence += (frame.EngineRumble - _enginePresence) * EnginePresenceRate;
                if (_enginePresence >= EngineOnThreshold) _enginePresent = true;
                else if (_enginePresence <= EngineOffThreshold) _enginePresent = false;
            }

            yield return Active
                ? frame
                : new StarCitizenTelemetryFrame { Source = frame.Source, Timestamp = frame.Timestamp };
        }
    }

    public async Task<IReadOnlyList<string>> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var innerDiagnostics = await _inner.GetDiagnosticsAsync(cancellationToken);
        var gate = new List<string>(_context.Diagnostics());
        if (_foreground is not null)
        {
            gate.AddRange(_foreground.Diagnostics());
        }

        if (_engineGate)
        {
            gate.Add($"Engine presence: {_enginePresence:0.00} (gate {(EnginePresent ? "open" : "closed")}; on>={EngineOnThreshold:0.00}/off<={EngineOffThreshold:0.00})");
        }

        return [.. gate, .. innerDiagnostics];
    }

    public async ValueTask DisposeAsync()
    {
        if (_foreground is not null)
        {
            await _foreground.DisposeAsync();
        }

        await _context.DisposeAsync();
        await _inner.DisposeAsync();
    }
}
