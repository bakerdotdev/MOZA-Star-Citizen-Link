using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MozaStarCitizen.App.Telemetry;

/// <summary>
/// Tracks whether Star Citizen is the focused (foreground) window, so force
/// feedback can be gated to only fire while the player is actually looking at
/// the game. Polls quickly (~250 ms) so alt-tabbing away pauses FFB promptly.
///
/// Fails open: if focus can't be determined, it reports focused so real play is
/// never wrongly muted.
/// </summary>
public sealed class ForegroundWatcher : IAsyncDisposable
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    private readonly string _processName;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _focused = true;

    public ForegroundWatcher(string processName = "StarCitizen")
    {
        _processName = processName;
    }

    public bool IsFocused => _focused;

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _focused = CheckFocused();
            try
            {
                await Task.Delay(250, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool CheckFocused()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0)
            {
                return false;
            }

            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals(_processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // Foreground process exited between the handle and the lookup.
            return false;
        }
        catch (Exception)
        {
            // Can't tell -> fail open so real play isn't muted.
            return true;
        }
    }

    public IReadOnlyList<string> Diagnostics() =>
    [
        $"SC focused (FFB enabled): {_focused}"
    ];

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try
            {
                if (_loop is not null)
                {
                    await _loop;
                }
            }
            catch
            {
                // ignore shutdown races
            }

            _cts.Dispose();
            _cts = null;
        }
    }
}
