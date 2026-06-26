using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using MozaStarCitizen.App.Diagnostics;

namespace MozaStarCitizen.App.Telemetry;

/// <summary>
/// Watches Star Citizen's Game.log (and the SC process) to decide whether the
/// player is in an active flight context. Used to gate audio-derived FFB so it
/// doesn't fire from menu music or while SC is closed.
///
/// Designed to FAIL OPEN: when the context is uncertain (e.g. no log found), it
/// reports in-flight whenever SC is running, so real flight feedback is never
/// wrongly muted. It only suppresses on high-confidence "not flying" states
/// (SC closed, returned to menu, or shut down).
/// </summary>
public sealed class GameLogContextWatcher : IAsyncDisposable
{
    private static readonly Regex ModeRegex =
        new(@"Changing game mode from .* to ([A-Za-z_]+)\[(-?\d+)\]", RegexOptions.Compiled);
    private static readonly Regex VehicleSpawn =
        new(@"\[VEHICLE SPAWN\].*OnVehicleSpawned", RegexOptions.Compiled);

    private readonly string? _logPath;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _pos;
    private bool _atMenu;
    private bool _shutdown;
    private string _mode = "(unknown)";
    private volatile bool _scRunning;
    private string _status = "Not started.";

    public GameLogContextWatcher(string? logPath = null)
    {
        _logPath = string.IsNullOrWhiteSpace(logPath) ? ResolveGameLog() : logPath;
    }

    public string Status => _status;

    public bool InFlight
    {
        get
        {
            if (!_scRunning) return false;       // SC not running -> definitely not flying
            if (_logPath is null) return true;   // no log -> fail open (allow whenever SC runs)
            lock (_sync)
            {
                // Fail open. Current SC builds emit no stable "entered pilot seat"
                // marker (only a ClearDriver on exit), so requiring a positive
                // vehicle-spawn event made the gate stick CLOSED for the whole
                // flight. Instead, allow feedback whenever SC is running and only
                // suppress on high-confidence not-flying states (shutdown, or a
                // confirmed return to the menu on builds that still log it). Fine
                // gating — on foot, in menus — is handled by the engine-presence
                // and focus gates in ContextGatedTelemetrySource.
                return !_shutdown && !_atMenu;
            }
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _status = _logPath is null
            ? "No Game.log found; gating on SC-running only (set MOZA_SC_GAMELOG to refine)."
            : $"Watching {_logPath}";
        AppLog.Write($"Context watcher: {_status}");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _scRunning = Process.GetProcessesByName("StarCitizen").Length > 0;
                if (!_scRunning)
                {
                    lock (_sync) { _atMenu = false; _shutdown = false; }
                }
                else if (_logPath is not null)
                {
                    ReadNewLines();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Context watcher poll failed: {ex.Message}");
            }

            try { await Task.Delay(TimeSpan.FromMilliseconds(1500), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void ReadNewLines()
    {
        var fi = new FileInfo(_logPath!);
        if (!fi.Exists) return;
        if (fi.Length < _pos)
        {
            // Log was truncated/replaced -> new SC session.
            _pos = 0;
            lock (_sync) { _atMenu = false; _shutdown = false; _mode = "(unknown)"; }
        }

        using var fs = new FileStream(_logPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(_pos, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            ProcessLine(line);
        }

        _pos = fs.Position;
    }

    private void ProcessLine(string line)
    {
        lock (_sync)
        {
            if (line.Contains("FastShutdown", StringComparison.Ordinal))
            {
                _shutdown = true;
                return;
            }

            // Old-format master-mode marker. Kept as a refinement: on builds that
            // still log it, INVALID/-1 means the frontend menu (suppress); a real
            // mode means we left the menu. Absent in current builds, which is fine
            // because the gate fails open.
            var m = ModeRegex.Match(line);
            if (m.Success)
            {
                _mode = m.Groups[1].Value;
                _shutdown = false;
                _atMenu = _mode == "INVALID" || m.Groups[2].Value == "-1";
                return;
            }

            // A vehicle spawn or in-game client spawn confirms we're past the menu.
            if (VehicleSpawn.IsMatch(line) || line.Contains("OnClientSpawned", StringComparison.Ordinal))
            {
                _atMenu = false;
                _shutdown = false;
            }
        }
    }

    public IReadOnlyList<string> Diagnostics()
    {
        lock (_sync)
        {
            return
            [
                $"Context gate source: {_logPath ?? "(SC-running only)"}",
                $"SC running: {_scRunning}; mode: {_mode}; at menu: {_atMenu}; shutdown: {_shutdown} (gate fails open; fine gating via engine+focus)",
                $"In-flight (FFB enabled): {InFlight}"
            ];
        }
    }

    private static string? ResolveGameLog()
    {
        var env = Environment.GetEnvironmentVariable("MOZA_SC_GAMELOG");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        try
        {
            var sc = Process.GetProcessesByName("StarCitizen").FirstOrDefault();
            var exe = sc?.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var candidate = Path.Combine(Path.GetDirectoryName(exe)!, "Game.log");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // EAC blocks MainModule access for a protected process; fall through.
        }

        foreach (var root in new[]
        {
            @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE",
            @"D:\StarCitizen\LIVE",
            @"C:\StarCitizen\LIVE",
            @"D:\Games\StarCitizen\LIVE",
            @"C:\Games\StarCitizen\LIVE"
        })
        {
            var candidate = Path.Combine(root, "Game.log");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try { if (_loop is not null) { await _loop; } } catch { /* ignore */ }
            _cts.Dispose();
            _cts = null;
        }
    }
}
