using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MozaStarCitizen.App.Diagnostics;
using MozaStarCitizen.App.ForceFeedback;
using MozaStarCitizen.App.Telemetry;

namespace MozaStarCitizen.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ForceFeedbackController _feedback;
    private readonly IStarCitizenTelemetrySource _telemetry;
    private CancellationTokenSource? _monitoringCancellation;
    private Task? _monitoringTask;
    private string _status = "Ready.";
    private bool _isMonitoring;
    private bool _isStarting;
    private long _framesReceived;
    private long _framesWithSignals;
    private long _forceUpdates;

    public MainViewModel()
    {
        _feedback = new ForceFeedbackController(ForceFeedbackDeviceFactory.Create());
        _telemetry = TelemetrySourceFactory.Create();

        StartCommand = new RelayCommand(_ => StartAsync(), _ => !IsMonitoring && !_isStarting);
        StopCommand = new RelayCommand(_ => StopAsync(), _ => IsMonitoring);
        StopEffectsCommand = new RelayCommand(_ => StopEffectsAsync());
        TestEngineCommand = new RelayCommand(_ => TestAsync(TestTelemetrySignal.Engine));
        TestImpactCommand = new RelayCommand(_ => TestAsync(TestTelemetrySignal.Impact));
        TestBoostCommand = new RelayCommand(_ => TestAsync(TestTelemetrySignal.Boost));
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

    public ICommand TestEngineCommand { get; }

    public ICommand TestImpactCommand { get; }

    public ICommand TestBoostCommand { get; }

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

    private async Task StartAsync()
    {
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

        _framesReceived = 0;
        _framesWithSignals = 0;
        _forceUpdates = 0;
        _monitoringCancellation = new CancellationTokenSource();
        _monitoringTask = RunTelemetryLoopAsync(_monitoringCancellation.Token);
        IsMonitoring = true;
        AddEvent("Telemetry monitoring started. Waiting for D-BOX/official telemetry frames.");
        Status = BuildMonitoringStatus();
        OnPropertyChanged(nameof(TelemetryStatus));
    }

    private async Task StopAsync()
    {
        var wasMonitoring = IsMonitoring || _monitoringTask is not null;
        try
        {
            if (_monitoringCancellation is not null)
            {
                await _monitoringCancellation.CancelAsync();
                _monitoringCancellation.Dispose();
                _monitoringCancellation = null;
            }

            if (_monitoringTask is not null)
            {
                await _monitoringTask;
                _monitoringTask = null;
            }

            await _feedback.StopAllAsync(CancellationToken.None);
            IsMonitoring = false;
            Status = wasMonitoring ? "Telemetry monitoring stopped." : "Monitoring is not running; effects stopped.";
            AddEvent($"Telemetry stopped; received {_framesReceived} frame(s), mapped {_framesWithSignals} signal frame(s), applied {_forceUpdates} force update(s).");
            OnPropertyChanged(nameof(TelemetryStatus));
        }
        catch (OperationCanceledException)
        {
            IsMonitoring = false;
            Status = "Telemetry monitoring stopped.";
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Stop failed");
            IsMonitoring = false;
            Status = $"Telemetry stopped, but cleanup failed: {ex.Message}";
            AddEvent($"Stop warning: {ex.Message}");
        }
    }

    private async Task StopEffectsAsync()
    {
        await _feedback.StopAllAsync(CancellationToken.None);
        Status = "All active force-feedback effects stopped.";
        AddEvent($"{DateTimeOffset.Now:HH:mm:ss} Stopped all active effects.");
    }

    private async Task RunTelemetryLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _telemetry.ReadFramesAsync(cancellationToken))
            {
                var frames = Interlocked.Increment(ref _framesReceived);
                if (frame.HasAnySignal)
                {
                    Interlocked.Increment(ref _framesWithSignals);
                }

                var result = await _feedback.HandleTelemetryAsync(frame, cancellationToken);
                if (!result.Contains("no force update", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _forceUpdates);
                    var timestamp = frame.Timestamp.ToLocalTime();
                    Dispatch(() => AddEvent($"{timestamp:HH:mm:ss.fff} {result}"));
                }

                if (frames <= 5 || frames % 120 == 0)
                {
                    Dispatch(() =>
                    {
                        Status = BuildMonitoringStatus();
                        OnPropertyChanged(nameof(TelemetryStatus));
                    });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "Telemetry loop failed");
            Dispatch(() =>
            {
                IsMonitoring = false;
                Status = $"Telemetry loop failed: {ex.Message}";
                AddEvent($"Telemetry loop failed: {ex.Message}");
                OnPropertyChanged(nameof(TelemetryStatus));
            });
        }
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
            TestTelemetrySignal.Engine => new StarCitizenTelemetryFrame
            {
                Source = "Manual test",
                EngineRumble = 0.42,
                EngineFrequencyHz = 32,
                GForceLongitudinal = 0.15
            },
            TestTelemetrySignal.Impact => new StarCitizenTelemetryFrame
            {
                Source = "Manual test",
                Impact = 0.85
            },
            TestTelemetrySignal.Boost => new StarCitizenTelemetryFrame
            {
                Source = "Manual test",
                Boost = 0.9,
                EngineRumble = 0.55,
                EngineFrequencyHz = 44
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

        Status = "Diagnostics refreshed.";
        OnPropertyChanged(nameof(TelemetryStatus));
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
        $"Monitoring telemetry. Frames: {_framesReceived}; signal frames: {_framesWithSignals}; force updates: {_forceUpdates}.";

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
        _ = StopAsync();
        _ = _telemetry.DisposeAsync();
    }

    private enum TestTelemetrySignal
    {
        Engine,
        Impact,
        Boost
    }
}
