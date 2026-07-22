using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MozaStarCitizen.App.Diagnostics;
using MozaStarCitizen.App.ForceFeedback;
using MozaStarCitizen.App.Telemetry;
using MozaStarCitizen.App.Telemetry.DBoxSdkLog;

namespace MozaStarCitizen.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable, IAsyncDisposable
{
    private readonly ForceFeedbackController _feedback;
    private readonly IStarCitizenTelemetrySource _telemetry;
    private readonly bool _visualizationOnly;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _startTask;
    private Task? _stopTask;
    private Task? _monitoringTask;
    private Task? _disposeTask;
    private string _status = "Ready.";
    private bool _isMonitoring;
    private bool _isStarting;
    private bool _isStopping;
    private bool _isDisposing;
    private long _framesReceived;
    private long _framesWithSignals;
    private long _forceUpdates;
    private long _samplePreviewFrames;

    public MainViewModel()
    {
        _telemetry = TelemetrySourceFactory.Create();
        _visualizationOnly =
            _telemetry.OutputPolicy == TelemetryOutputPolicy.VisualizationOnly;
        _feedback = new ForceFeedbackController(
            ForceFeedbackDeviceFactory.Create(_telemetry.OutputPolicy));

        StartCommand = new RelayCommand(
            _ => StartAsync(),
            _ => !IsMonitoring && !_isStarting && !_isStopping && !_isDisposing);
        StopCommand = new RelayCommand(
            _ => StopAsync(),
            _ => IsMonitoring && !_isStopping);
        StopEffectsCommand = new RelayCommand(_ => StopEffectsAsync());
        TestAfterburnerCommand = new RelayCommand(_ => TestAsync(TestTelemetrySignal.Afterburner));
        TestAtmosphereCommand = new RelayCommand(_ => TestAsync(TestTelemetrySignal.Atmosphere));
        RefreshDiagnosticsCommand = new RelayCommand(_ => RefreshDiagnosticsAsync());

        OutputName = _feedback.OutputName;
        OutputStatus = _feedback.OutputStatus;
        TelemetryName = _telemetry.Name;
        RefreshDiagnostics(includeExtendedDiagnostics: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            if (SetField(ref _isMonitoring, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string OutputName { get; }

    public string OutputStatus { get; }

    public string TelemetryName { get; }

    public string TelemetryStatus => _telemetry.Status;

    public ObservableCollection<string> Events { get; } = [];

    public ObservableCollection<string> Diagnostics { get; } = [];

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand StopEffectsCommand { get; }

    public ICommand TestAfterburnerCommand { get; }

    public ICommand TestAtmosphereCommand { get; }

    public ICommand RefreshDiagnosticsCommand { get; }

    public async Task AutoStartAsync()
    {
        if (IsMonitoring || _isStarting)
        {
            return;
        }

        AppLog.Write("Auto-starting telemetry monitor.");
        await StartAsync();
    }

    private Task StartAsync()
    {
        if (_startTask is { IsCompleted: false } activeStart)
        {
            return activeStart;
        }

        _startTask = StartCoreAsync();
        return _startTask;
    }

    private async Task StartCoreAsync()
    {
        if (_isDisposing)
        {
            Status = "The application is closing; telemetry was not started.";
            return;
        }

        if (_isStopping)
        {
            Status = "Telemetry is still stopping.";
            return;
        }

        if (IsMonitoring || _isStarting)
        {
            Status = BuildMonitoringStatus();
            return;
        }

        _isStarting = true;
        RaiseCommandStates();
        Status = "Starting telemetry monitor.";

        try
        {
            await _feedback.InitializeAsync(CancellationToken.None);
            await _telemetry.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Telemetry monitor failed to start");
            Status = $"Telemetry monitor failed to start: {ex.Message}";
            return;
        }
        finally
        {
            _isStarting = false;
            RaiseCommandStates();
        }

        if (_isDisposing)
        {
            await _feedback.StopAllAsync(CancellationToken.None);
            Status = "The application is closing; telemetry startup was canceled.";
            return;
        }

        _framesReceived = 0;
        _framesWithSignals = 0;
        _forceUpdates = 0;
        _samplePreviewFrames = 0;
        var session = new CancellationTokenSource();
        _monitoringCancellation = session;
        IsMonitoring = true;
        AddEvent("Telemetry monitoring started. Waiting for D-BOX/official telemetry frames.");
        Status = BuildMonitoringStatus();
        OnPropertyChanged(nameof(TelemetryStatus));
        _monitoringTask = RunTelemetryLoopAsync(session);
    }

    private Task StopAsync()
    {
        if (_stopTask is { IsCompleted: false } activeStop)
        {
            return activeStop;
        }

        _stopTask = StopCoreAsync();
        return _stopTask;
    }

    private async Task StopCoreAsync()
    {
        _isStopping = true;
        RaiseCommandStates();
        var wasMonitoring = IsMonitoring || _monitoringTask is not null;
        var session = _monitoringCancellation;
        var monitoringTask = _monitoringTask;
        Exception? cleanupFailure = null;
        try
        {
            if (session is not null)
            {
                try
                {
                    await session.CancelAsync();
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                    AppLog.Write(ex, "Telemetry cancellation failed");
                }
            }

            if (monitoringTask is not null)
            {
                try
                {
                    await monitoringTask;
                }
                catch (OperationCanceledException) when (
                    session?.IsCancellationRequested == true)
                {
                }
                catch (Exception ex)
                {
                    cleanupFailure ??= ex;
                    AppLog.Write(ex, "Telemetry task cleanup failed");
                }
            }

            try
            {
                await _feedback.StopAllAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                cleanupFailure ??= ex;
                AppLog.Write(ex, "Force-feedback cleanup failed");
            }

            if (cleanupFailure is null)
            {
                Status = wasMonitoring
                    ? "Telemetry monitoring stopped."
                    : "Monitoring is not running; effects stopped.";
                AddEvent(
                    $"Telemetry stopped; received {_framesReceived} frame(s), " +
                    $"mapped {_framesWithSignals} signal frame(s), " +
                    $"applied {_forceUpdates} force update(s).");
            }
            else
            {
                Status = $"Telemetry stopped, but cleanup failed: {cleanupFailure.Message}";
                AddEvent($"Stop warning: {cleanupFailure.Message}");
            }

            OnPropertyChanged(nameof(TelemetryStatus));
        }
        finally
        {
            var newerSessionExists =
                _monitoringCancellation is not null &&
                !ReferenceEquals(_monitoringCancellation, session);
            if (ReferenceEquals(_monitoringCancellation, session))
            {
                _monitoringCancellation = null;
                if (ReferenceEquals(_monitoringTask, monitoringTask))
                {
                    _monitoringTask = null;
                }

                session?.Dispose();
            }

            if (!newerSessionExists)
            {
                IsMonitoring = false;
            }

            _isStopping = false;
            RaiseCommandStates();
        }
    }

    private async Task StopEffectsAsync()
    {
        await _feedback.StopAllAsync(CancellationToken.None);
        Status = "All active force-feedback effects stopped.";
        AddEvent($"{DateTimeOffset.Now:HH:mm:ss} Stopped all active effects.");
    }

    private async Task RunTelemetryLoopAsync(CancellationTokenSource session)
    {
        // Ensure StartAsync assigns the task before a zero-delay replay can finish.
        await Task.Yield();
        var cancellationToken = session.Token;

        try
        {
            await foreach (var frame in _telemetry.ReadFramesAsync(cancellationToken))
            {
                var frames = Interlocked.Increment(ref _framesReceived);
                if (frame.UpdatedSignals != TelemetrySignalSet.None || frame.HasAnySignal)
                {
                    Interlocked.Increment(ref _framesWithSignals);
                }

                if (_visualizationOnly)
                {
                    if (DBoxSdkSampleFramePreview.ShouldDisplay(frame, frames) &&
                        DBoxSdkSampleFramePreview.TryFormat(frame, out var preview))
                    {
                        Interlocked.Increment(ref _samplePreviewFrames);
                        var previewTimestamp = frame.Timestamp.ToLocalTime();
                        Dispatch(() =>
                            AddEvent($"{previewTimestamp:HH:mm:ss.fff} Preview: {preview}"));
                    }
                }

                if (!_visualizationOnly)
                {
                    string result;
                    try
                    {
                        result = await _feedback.HandleTelemetryAsync(frame, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // A single frame's force update failing (e.g. the stick was
                        // power-cycled) must not tear down the whole monitor; log and
                        // keep consuming telemetry so output resumes when it recovers.
                        AppLog.Write(ex, "Force-feedback update failed for a frame; continuing");
                        continue;
                    }

                    if (!result.Contains("no force update", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _forceUpdates);
                        var timestamp = frame.Timestamp.ToLocalTime();
                        Dispatch(() => AddEvent($"{timestamp:HH:mm:ss.fff} {result}"));
                    }
                }

                if (frames <= 5 || frames % 120 == 0)
                {
                    // Periodic telemetry snapshot to the log so flight behaviour can
                    // be diagnosed without clicking Refresh (which unfocuses SC and
                    // skews the reading). Shows the gate state + live audio levels.
                    AppLog.Write($"TELEMETRY: {_telemetry.Status}");
                    Dispatch(() =>
                    {
                        Status = BuildMonitoringStatus();
                        OnPropertyChanged(nameof(TelemetryStatus));
                    });
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                await _feedback.StopAllAsync(CancellationToken.None);
                var completionStatus = _visualizationOnly
                    ? "D-BOX SDK sample-log replay completed."
                    : "Telemetry stream completed.";
                var completionEvent = _visualizationOnly
                    ? $"Replay completed; received {_framesReceived} frame(s), displayed {_samplePreviewFrames} preview entries, and applied no hardware force updates."
                    : $"Telemetry stream completed after {_framesReceived} frame(s) and {_forceUpdates} force update(s).";
                CompleteMonitoringSession(
                    session,
                    completionStatus,
                    completionEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Telemetry loop failed");
            try
            {
                await _feedback.StopAllAsync(CancellationToken.None);
            }
            catch (Exception stopException)
            {
                AppLog.Write(stopException, "Effect cleanup after telemetry failure also failed");
            }

            CompleteMonitoringSession(
                session,
                $"Telemetry loop failed: {ex.Message}",
                $"Telemetry loop failed: {ex.Message}");
        }
    }

    private void CompleteMonitoringSession(
        CancellationTokenSource session,
        string status,
        string eventMessage)
    {
        Dispatch(() =>
        {
            if (!ReferenceEquals(_monitoringCancellation, session))
            {
                return;
            }

            _monitoringCancellation = null;
            _monitoringTask = null;
            session.Dispose();
            IsMonitoring = false;
            Status = status;
            AddEvent(eventMessage);
            OnPropertyChanged(nameof(TelemetryStatus));
        });
    }

    private async Task TestAsync(TestTelemetrySignal signal)
    {
        try
        {
            await _feedback.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"Force feedback output failed to initialize: {ex.Message}";
            return;
        }

        var frame = signal switch
        {
            TestTelemetrySignal.Afterburner => new StarCitizenTelemetryFrame
            {
                Source = "Manual test",
                Afterburner = 1.0
            },
            TestTelemetrySignal.Atmosphere => new StarCitizenTelemetryFrame
            {
                Source = "Manual test",
                Atmosphere = 0.8
            },
            _ => new StarCitizenTelemetryFrame { Source = "Manual test" }
        };

        try
        {
            var result = await _feedback.HandleTelemetryAsync(frame, CancellationToken.None);
            AddEvent($"{frame.Timestamp:HH:mm:ss.fff} Manual {signal}: {result}");
            Status = result;
        }
        catch (Exception ex)
        {
            Status = $"Force feedback output failed: {ex.Message}";
            AddEvent($"{frame.Timestamp:HH:mm:ss.fff} Manual {signal}: failed - {ex.Message}");
        }
    }

    private void AddEvent(string message)
    {
        Events.Insert(0, message);
        while (Events.Count > 200)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        Status = "Refreshing diagnostics.";
        Diagnostics.Clear();
        AddRuntimeDiagnostics();
        Diagnostics.Add("Running extended diagnostics...");

        var diagnosticTask = Task.Run(() =>
            ForceFeedbackDiagnostics.GetLines(_feedback.Device, includeExtendedDiagnostics: true));
        var telemetryTask = _telemetry.GetDiagnosticsAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(4));
        var completedTask = await Task.WhenAny(Task.WhenAll(diagnosticTask, telemetryTask), timeoutTask);

        Diagnostics.Clear();
        AddRuntimeDiagnostics();
        if (completedTask == timeoutTask)
        {
            foreach (var line in ForceFeedbackDiagnostics.GetLines(_feedback.Device, includeExtendedDiagnostics: false))
            {
                Diagnostics.Add(line);
            }

            Diagnostics.Add("Extended diagnostics timed out.");
            PersistDiagnostics();
            Status = "Diagnostics timed out.";
            return;
        }

        foreach (var line in await diagnosticTask)
        {
            Diagnostics.Add(line);
        }

        foreach (var line in await telemetryTask)
        {
            Diagnostics.Add(line);
        }

        PersistDiagnostics();
        Status = "Diagnostics refreshed.";
        OnPropertyChanged(nameof(TelemetryStatus));
    }

    private void PersistDiagnostics()
    {
        AppLog.WriteDiagnostics(Diagnostics);
        Diagnostics.Add($"Saved to: {AppLog.DiagnosticsPath}");
    }

    private void RefreshDiagnostics(bool includeExtendedDiagnostics)
    {
        Diagnostics.Clear();
        AddRuntimeDiagnostics();
        foreach (var line in ForceFeedbackDiagnostics.GetLines(_feedback.Device, includeExtendedDiagnostics))
        {
            Diagnostics.Add(line);
        }

        Diagnostics.Add($"Telemetry source: {_telemetry.Name}");
        Diagnostics.Add($"Telemetry output policy: {_telemetry.OutputPolicy}");
        Diagnostics.Add($"Telemetry status: {_telemetry.Status}");
    }

    private void AddRuntimeDiagnostics()
    {
        Diagnostics.Add($"App log file: {AppLog.LogPath}");
        Diagnostics.Add($"Telemetry mode: {Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY") ?? "Auto"}");
        Diagnostics.Add($"Telemetry URL: {Environment.GetEnvironmentVariable("MOZA_SC_TELEMETRY_URL") ?? "(none)"}");
        Diagnostics.Add($"Telemetry frames/signals/updates: {_framesReceived}/{_framesWithSignals}/{_forceUpdates}");
    }

    private string BuildMonitoringStatus() =>
        _visualizationOnly
            ? $"Previewing telemetry. Frames: {_framesReceived}; signal frames: {_framesWithSignals}; preview entries: {_samplePreviewFrames}; hardware output disabled."
            : $"Monitoring telemetry. Frames: {_framesReceived}; signal frames: {_framesWithSignals}; force updates: {_forceUpdates}.";

    private void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { StartCommand, StopCommand })
        {
            if (command is RelayCommand relayCommand)
            {
                relayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void Dispose()
    {
        _ = DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _isDisposing = true;
        RaiseCommandStates();
        return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        if (_startTask is { } startTask)
        {
            try
            {
                await startTask;
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "Telemetry startup failed during shutdown");
            }
        }

        await StopAsync();
        try
        {
            await _telemetry.DisposeAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Telemetry source disposal failed");
        }
    }

    private enum TestTelemetrySignal
    {
        Afterburner,
        Atmosphere
    }
}
