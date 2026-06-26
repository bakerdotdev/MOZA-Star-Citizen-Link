using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MozaStarCitizen.App.Diagnostics;
using MozaStarCitizen.App.ForceFeedback.DirectInput;
using MozaStarCitizen.App.Models;

namespace MozaStarCitizen.App.ForceFeedback;

/// <summary>
/// DirectInput force-feedback output that survives the stick being powered off
/// and back on without an app restart. A background monitor reconciles the live
/// hardware with the open device handle: when the stick disappears it tears the
/// handle down; when it reappears (possibly under a new instance GUID) it
/// re-enumerates, re-opens, re-acquires, and re-creates the cached effects.
///
/// While detached, <see cref="PlayAsync"/>/<see cref="StopAsync"/> are no-ops
/// (they never throw), so the <see cref="FallbackForceFeedbackDevice"/> wrapper
/// never permanently demotes to the null output and the telemetry loop keeps
/// running until the hardware comes back.
/// </summary>
public sealed class DirectInputForceFeedbackDevice : IForceFeedbackDevice
{
    private readonly Dictionary<string, IDirectInputEffect> _activeEffects = [];
    private readonly Dictionary<string, IDirectInputEffect> _effectCache = [];
    private readonly object _sync = new();

    // Effects the controller asked us to prepare. Remembered so they can be
    // re-created on the rebuilt device after a reconnect.
    private List<ForceEffect> _preparedEffects = [];

    private Guid _instanceGuid;
    private string _productName;
    private IDirectInput8W? _directInput;
    private IDirectInputDevice8W? _device;
    private IntPtr _windowHandle = IntPtr.Zero;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _monitorStarted;
    private bool _everAttached;
    private volatile bool _faulted;
    private bool _faultLogged;
    private int _consecutiveMisses;

    private DirectInputForceFeedbackDevice(DirectInputDeviceInfo? deviceInfo)
    {
        if (deviceInfo is null)
        {
            _instanceGuid = Guid.Empty;
            _productName = "force-feedback device (waiting)";
        }
        else
        {
            _instanceGuid = deviceInfo.InstanceGuid;
            _productName = string.IsNullOrWhiteSpace(deviceInfo.ProductName)
                ? deviceInfo.InstanceName
                : deviceInfo.ProductName;
        }
    }

    public string Name
    {
        get
        {
            lock (_sync)
            {
                return _device is null
                    ? $"DirectInput: {_productName} (searching)"
                    : $"DirectInput: {_productName}";
            }
        }
    }

    public string Status => "Using Windows DirectInput force feedback (auto-reconnects on power-cycle).";

    public static IForceFeedbackDevice? CreateIfAvailable()
    {
        try
        {
            var devices = DirectInputNative.EnumerateForceFeedbackDevices();
            AppLog.Write($"DirectInput force-feedback enumeration found {devices.Count} device(s): {string.Join("; ", devices.Select(DisplayName))}");

            // Always return a self-healing device, even when nothing is attached
            // yet: the monitor will attach the stick when it appears (or comes
            // back after a power-cycle).
            return new DirectInputForceFeedbackDevice(SelectPreferred(devices));
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "DirectInput force-feedback enumeration failed");
            return null;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Capture the main-window handle on the (UI) calling thread; the monitor
        // reuses it on background-thread reattach, where Application.MainWindow
        // is not accessible.
        _windowHandle = GetMainWindowHandle();

        // Attach immediately if the stick is present, so effects are ready right
        // after initialization (matching the previous single-shot behaviour).
        if (_device is null)
        {
            var preferred = SelectPreferred(SafeEnumerate());
            if (preferred is not null)
            {
                TryAttach(preferred);
            }
        }

        StartMonitor();
        return Task.CompletedTask;
    }

    public Task PrepareAsync(IEnumerable<ForceEffect> effects, CancellationToken cancellationToken)
    {
        var prepared = effects.ToList();
        lock (_sync)
        {
            _preparedEffects = prepared;
            if (_device is null)
            {
                // Will be created when the device attaches.
                return Task.CompletedTask;
            }

            foreach (var effect in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = GetOrCreateEffect(effect);
            }

            AppLog.Write($"DirectInput prepared {_effectCache.Count} cached effect(s) for '{_productName}'.");
        }

        return Task.CompletedTask;
    }

    public Task PlayAsync(ForceEffect effect, CancellationToken cancellationToken)
    {
        string? transientKey = null;
        TimeSpan transientDuration = default;

        lock (_sync)
        {
            if (_device is null)
            {
                // Detached (powered off / not yet connected). Silently no-op so
                // the fallback wrapper stays on us and the loop keeps running;
                // the monitor reattaches when the hardware returns.
                return Task.CompletedTask;
            }

            var key = effect.StateKey ?? $"transient-{Guid.NewGuid():N}";

            try
            {
                var alreadyRunning = _activeEffects.ContainsKey(key);

                // One DirectInput effect per logical state (engine, atmosphere,
                // impact, ...), updated in place. Creating a fresh effect every
                // telemetry frame (the old behaviour) leaked downloaded effects
                // until the device ran out of slots and CreateEffect faulted.
                var directInputEffect = GetOrCreateEffect(effect);

                AppLog.Write($"DirectInput effect '{effect.Name}' on '{_productName}' intensity={effect.Intensity:0.###} durationMs={effect.Duration.TotalMilliseconds:0} frequencyHz={effect.FrequencyHz:0.###}.");
                ApplyParameters(directInputEffect, effect, start: !alreadyRunning);

                _activeEffects[key] = directInputEffect;
                _faultLogged = false;

                if (effect.StateKey is null && effect.Duration > TimeSpan.Zero)
                {
                    transientKey = key;
                    transientDuration = effect.Duration;
                }
            }
            catch (Exception ex)
            {
                // The handle likely died under us (stick powered off mid-frame).
                // Don't throw — flag the monitor to verify/recover and no-op.
                SignalFault(ex);
            }
        }

        if (transientKey is not null)
        {
            _ = StopTransientAsync(transientKey, transientDuration);
        }

        return Task.CompletedTask;
    }

    private IDirectInputEffect GetOrCreateEffect(ForceEffect effect)
    {
        var cacheKey = GetCacheKey(effect);
        lock (_sync)
        {
            if (_effectCache.TryGetValue(cacheKey, out var cachedEffect))
            {
                return cachedEffect;
            }
        }

        var createdEffect = CreateEffect(effect);

        lock (_sync)
        {
            if (_effectCache.TryGetValue(cacheKey, out var cachedEffect))
            {
                ReleaseEffect(createdEffect);
                return cachedEffect;
            }

            _effectCache[cacheKey] = createdEffect;
        }

        return createdEffect;
    }

    public Task StopAsync(string stateKey, CancellationToken cancellationToken)
    {
        StopEffect(stateKey);
        return Task.CompletedTask;
    }

    public Task StopAllAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            foreach (var key in _activeEffects.Keys.ToList())
            {
                StopEffectLocked(key);
            }

            if (_device is not null)
            {
                try
                {
                    _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcStopAll);
                }
                catch (Exception ex)
                {
                    SignalFault(ex);
                }
            }
        }

        return Task.CompletedTask;
    }

    private IDirectInputEffect CreateEffect(ForceEffect effect)
    {
        var asConstant = effect.Kind is ForceEffectKind.Bump or ForceEffectKind.ConstantForce;
        try
        {
            return BuildOrUpdate(effect, asConstant, existing: null, start: false);
        }
        catch (Exception ex) when (asConstant)
        {
            AppLog.Write(ex, $"DirectInput constant-force creation failed for '{effect.Name}'. Falling back to a sine pulse.");
            return BuildOrUpdate(
                effect with { FrequencyHz = effect.FrequencyHz <= 0 ? 42 : effect.FrequencyHz },
                asConstant: false,
                existing: null,
                start: false);
        }
    }

    private void ApplyParameters(IDirectInputEffect directInputEffect, ForceEffect effect, bool start)
    {
        var asConstant = effect.Kind is ForceEffectKind.Bump or ForceEffectKind.ConstantForce;
        _ = BuildOrUpdate(effect, asConstant, existing: directInputEffect, start);
    }

    // Builds the DIEFFECT (+ type-specific params) for a spec and either creates
    // a new device effect (existing == null) or updates an existing one in place
    // via SetParameters. Reusing one effect per logical state and updating it is
    // what keeps the downloaded-effect count bounded instead of leaking until the
    // device overruns and CreateEffect faults.
    private IDirectInputEffect BuildOrUpdate(ForceEffect effect, bool asConstant, IDirectInputEffect? existing, bool start)
    {
        EnsureInitialized();

        var typeSpecificSize = asConstant
            ? Marshal.SizeOf<DirectInputConstantForce>()
            : Marshal.SizeOf<DirectInputPeriodic>();

        var axes = IntPtr.Zero;
        var direction = IntPtr.Zero;
        var typeSpecific = IntPtr.Zero;

        try
        {
            axes = Marshal.AllocHGlobal(sizeof(int) * 2);
            direction = Marshal.AllocHGlobal(sizeof(int) * 2);
            typeSpecific = Marshal.AllocHGlobal(typeSpecificSize);

            Marshal.WriteInt32(axes, 0, DirectInputConstants.DijoFsX);
            Marshal.WriteInt32(axes, sizeof(int), DirectInputConstants.DijoFsY);
            Marshal.WriteInt32(direction, 0, 1);
            Marshal.WriteInt32(direction, sizeof(int), 1);

            if (asConstant)
            {
                Marshal.StructureToPtr(
                    new DirectInputConstantForce { Magnitude = ScaleSignedMagnitude(effect.Intensity) },
                    typeSpecific,
                    false);
            }
            else
            {
                Marshal.StructureToPtr(
                    new DirectInputPeriodic
                    {
                        Magnitude = ScaleMagnitude(effect.Intensity),
                        Offset = 0,
                        Phase = 0,
                        Period = HertzToPeriod(effect.FrequencyHz)
                    },
                    typeSpecific,
                    false);
            }

            var dieffect = new DirectInputEffect
            {
                Size = Marshal.SizeOf<DirectInputEffect>(),
                Flags = DirectInputConstants.DieffCartesian | DirectInputConstants.DieffObjectOffsets,
                Duration = ToDirectInputDuration(effect.Duration),
                SamplePeriod = 0,
                Gain = DirectInputConstants.DiFfNominalMax,
                TriggerButton = DirectInputConstants.Infinite,
                TriggerRepeatInterval = 0,
                AxisCount = 2,
                Axes = axes,
                Direction = direction,
                Envelope = IntPtr.Zero,
                TypeSpecificParameterSize = typeSpecificSize,
                TypeSpecificParameters = typeSpecific,
                StartDelay = 0
            };

            if (existing is null)
            {
                var guid = asConstant ? DirectInputConstants.GuidConstantForce : DirectInputConstants.GuidSine;
                var createResult = _device!.CreateEffect(ref guid, ref dieffect, out var created, IntPtr.Zero);
                if (createResult == DirectInputConstants.DierrNotExclusiveAcquired)
                {
                    Reacquire();
                    createResult = _device.CreateEffect(ref guid, ref dieffect, out created, IntPtr.Zero);
                }

                DirectInputNative.ThrowIfFailed(createResult, $"DirectInput could not create '{effect.Name}'");
                DownloadEffect(created, effect.Name);
                return created;
            }

            // Only the mutable parameters may be changed via SetParameters.
            // DIEP_AXES (and direction) are fixed at creation — including DIEP_AXES
            // here makes the driver reject the update with ERROR_ALREADY_INITIALIZED
            // (0x800704DF). Update magnitude/period (type-specific), gain and duration.
            var flags = DirectInputConstants.DiepTypespecificparams
                | DirectInputConstants.DiepGain
                | DirectInputConstants.DiepDuration;
            if (start)
            {
                flags |= DirectInputConstants.DiepStart;
            }

            var result = existing.SetParameters(ref dieffect, flags);

            // Recovery retries. After a re-acquire (Unacquire unloads every effect)
            // or a re-download, the effect is NOT playing, so it must be re-armed
            // with DIEP_START regardless of the caller's 'start' hint — that hint
            // assumes the effect was still running, which is no longer true. Without
            // forcing start here, a sustained effect (engine rumble) recovers
            // downloaded-but-stopped and stays silent until its state next toggles.
            if (result == DirectInputConstants.DierrNotExclusiveAcquired)
            {
                Reacquire();
                DownloadEffect(existing, effect.Name);
                result = existing.SetParameters(ref dieffect, flags | DirectInputConstants.DiepStart);
            }
            else if (result == DirectInputConstants.DierrNotDownloaded)
            {
                DownloadEffect(existing, effect.Name);
                result = existing.SetParameters(ref dieffect, flags | DirectInputConstants.DiepStart);
            }

            DirectInputNative.ThrowIfFailed(result, $"DirectInput could not update '{effect.Name}'");
            return existing;
        }
        finally
        {
            FreeIfAllocated(axes);
            FreeIfAllocated(direction);
            FreeIfAllocated(typeSpecific);
        }
    }

    private async Task StopTransientAsync(string key, TimeSpan duration)
    {
        try
        {
            await Task.Delay(duration);
            StopEffect(key);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void StopEffect(string key)
    {
        lock (_sync)
        {
            StopEffectLocked(key);
        }
    }

    private void StopEffectLocked(string key)
    {
        if (!_activeEffects.Remove(key, out var effect))
        {
            return;
        }

        try
        {
            _ = effect.Stop();
        }
        catch
        {
            // The handle may have died; the monitor will detach/recover.
        }
    }

    private void EnsureInitialized()
    {
        if (_device is null)
        {
            throw new InvalidOperationException("DirectInput force feedback has not been initialized.");
        }
    }

    private void Reacquire()
    {
        EnsureInitialized();

        _ = _device!.Unacquire();
        var acquireResult = _device.Acquire();
        if (!DirectInputNative.Succeeded(acquireResult))
        {
            AppLog.Write($"DirectInput re-acquire returned 0x{acquireResult:X8} for '{_productName}'.");
        }

        _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcSetActuatorsOn);
    }

    private void DownloadEffect(IDirectInputEffect effect, string effectName)
    {
        var result = effect.Download();
        if (result == DirectInputConstants.DierrNotExclusiveAcquired)
        {
            AppLog.Write($"DirectInput lost exclusive acquisition for '{_productName}' while downloading '{effectName}'. Re-acquiring and retrying once.");
            Reacquire();
            result = effect.Download();
        }

        DirectInputNative.ThrowIfFailed(result, $"DirectInput could not download '{effectName}'");
    }

    // ---- Hot-plug reconciliation ------------------------------------------------

    private void StartMonitor()
    {
        lock (_sync)
        {
            if (_monitorStarted)
            {
                return;
            }

            _monitorStarted = true;
            _monitorCts = new CancellationTokenSource();
        }

        var token = _monitorCts!.Token;
        _monitorTask = Task.Run(() => MonitorAsync(token));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var detached = true;
            try
            {
                bool attached;
                lock (_sync)
                {
                    attached = _device is not null;
                }

                // Authoritative liveness check: re-run the same force-feedback
                // enumeration that found the device at startup. GetDeviceStatus
                // proved unreliable (it reported a live device as not-attached),
                // so presence in the enumeration is the source of truth. Done
                // outside the lock — it spins up and releases its own COM object.
                var present = SafeEnumerate();

                if (attached)
                {
                    if (_faulted)
                    {
                        InvokeOnDeviceThread(() => HandleFault(present));
                        detached = IsDetached();
                        _consecutiveMisses = 0;
                    }
                    else if (IsInstancePresent(present))
                    {
                        _consecutiveMisses = 0;
                        detached = false;
                    }
                    else if (++_consecutiveMisses >= 2)
                    {
                        // Two consecutive enumerations missed the device: it is
                        // really gone. One miss alone could be a transient driver
                        // hiccup, so we don't tear down on the first.
                        InvokeOnDeviceThread(() => Detach("device no longer attached (powered off or unplugged)"));
                        detached = true;
                        _consecutiveMisses = 0;
                    }
                    else
                    {
                        detached = false;
                    }
                }
                else
                {
                    _consecutiveMisses = 0;
                    var preferred = SelectPreferred(present);
                    if (preferred is null)
                    {
                        detached = true;
                    }
                    else
                    {
                        DirectInputDeviceInfo info = preferred;
                        var attachedNow = false;
                        InvokeOnDeviceThread(() => attachedNow = TryAttach(info));
                        detached = !attachedNow;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, "FFB device monitor");
            }

            // Poll faster while searching / just after a fault so reconnects feel
            // immediate; back off to 1 s once healthy.
            var delayMs = detached || _faulted ? 400 : 1000;
            try
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void HandleFault(IReadOnlyList<DirectInputDeviceInfo> present)
    {
        lock (_sync)
        {
            _faulted = false;
            if (_device is null)
            {
                return;
            }

            if (IsInstancePresentLocked(present))
            {
                // Device is still physically attached but a call failed (e.g.
                // lost exclusive acquisition) — recover in place.
                try
                {
                    Reacquire();
                }
                catch (Exception ex)
                {
                    AppLog.Write(ex, $"DirectInput re-acquire after fault failed for '{_productName}'; detaching");
                    DetachLocked("re-acquire after fault failed");
                }
            }
            else
            {
                DetachLocked("device fault and no longer attached");
            }
        }
    }

    private bool IsDetached()
    {
        lock (_sync)
        {
            return _device is null;
        }
    }

    private bool IsInstancePresent(IReadOnlyList<DirectInputDeviceInfo> present)
    {
        lock (_sync)
        {
            return IsInstancePresentLocked(present);
        }
    }

    private bool IsInstancePresentLocked(IReadOnlyList<DirectInputDeviceInfo> present)
    {
        if (_instanceGuid == Guid.Empty)
        {
            return false;
        }

        foreach (var device in present)
        {
            if (device.InstanceGuid == _instanceGuid)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryAttach(DirectInputDeviceInfo info)
    {
        lock (_sync)
        {
            if (_device is not null)
            {
                return true;
            }

            try
            {
                _instanceGuid = info.InstanceGuid;
                _productName = string.IsNullOrWhiteSpace(info.ProductName) ? info.InstanceName : info.ProductName;

                _directInput = DirectInputNative.CreateDirectInput();
                var guid = _instanceGuid;
                DirectInputNative.ThrowIfFailed(
                    _directInput.CreateDevice(ref guid, out _device, IntPtr.Zero),
                    $"DirectInput could not open '{_productName}'");

                DirectInputNative.SetTwoAxisJoystickDataFormat(_device, _productName);
                AppLog.Write($"DirectInput data format set for '{_productName}'.");

                var cooperativeFlags = DirectInputConstants.DiExclusive | DirectInputConstants.DiBackground;
                var cooperativeResult = _device.SetCooperativeLevel(_windowHandle, cooperativeFlags);
                if (!DirectInputNative.Succeeded(cooperativeResult))
                {
                    // Some drivers reject a null HWND for background exclusive mode. Effect creation may still work.
                    AppLog.Write($"DirectInput SetCooperativeLevel returned 0x{cooperativeResult:X8} for '{_productName}'.");
                }

                DirectInputNative.ThrowIfFailed(_device.Acquire(), $"DirectInput could not acquire '{_productName}'");
                _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcReset);
                _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcSetActuatorsOn);
                AppLog.Write($"DirectInput initialized '{_productName}'.");

                // Re-create the prepared effects on the fresh device.
                foreach (var effect in _preparedEffects)
                {
                    _ = GetOrCreateEffect(effect);
                }

                _faulted = false;
                _faultLogged = false;

                if (_everAttached)
                {
                    AppLog.Write($"DirectInput force-feedback device RECONNECTED: '{_productName}' ({_effectCache.Count} effect(s) restored).");
                }
                else
                {
                    AppLog.Write($"DirectInput force-feedback device CONNECTED: '{_productName}'.");
                }

                _everAttached = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write(ex, $"DirectInput attach failed for '{_productName}'");
                DetachLocked("attach failed");
                return false;
            }
        }
    }

    private void Detach(string reason)
    {
        lock (_sync)
        {
            DetachLocked(reason);
        }
    }

    private void DetachLocked(string reason)
    {
        var hadDevice = _device is not null;

        foreach (var effect in _effectCache.Values)
        {
            try
            {
                _ = effect.Stop();
                _ = effect.Unload();
                _ = Marshal.FinalReleaseComObject(effect);
            }
            catch
            {
                // Ignore — the underlying device is gone.
            }
        }

        _effectCache.Clear();
        _activeEffects.Clear();

        if (_device is not null)
        {
            try { _ = _device.Unacquire(); } catch { /* gone */ }
            try { _ = Marshal.FinalReleaseComObject(_device); } catch { /* gone */ }
            _device = null;
        }

        if (_directInput is not null)
        {
            try { _ = Marshal.FinalReleaseComObject(_directInput); } catch { /* gone */ }
            _directInput = null;
        }

        _faulted = false;

        if (hadDevice)
        {
            AppLog.Write($"DirectInput force-feedback device LOST ('{_productName}') - {reason}; will auto-reconnect when the device returns.");
        }
    }

    private void SignalFault(Exception ex)
    {
        _faulted = true;
        if (!_faultLogged)
        {
            _faultLogged = true;
            AppLog.Write(ex, $"DirectInput force-feedback call failed for '{_productName}' (will verify device and recover)");
        }
    }

    private static IReadOnlyList<DirectInputDeviceInfo> SafeEnumerate()
    {
        try
        {
            return DirectInputNative.EnumerateForceFeedbackDevices();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "DirectInput force-feedback re-enumeration failed");
            return [];
        }
    }

    private static DirectInputDeviceInfo? SelectPreferred(IReadOnlyList<DirectInputDeviceInfo> devices) =>
        devices
            .OrderByDescending(IsPreferredDevice)
            .ThenBy(d => d.ProductName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static int IsPreferredDevice(DirectInputDeviceInfo deviceInfo)
    {
        var text = $"{deviceInfo.ProductName} {deviceInfo.InstanceName}";
        return text.Contains("MOZA", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("AB6", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("AB9", StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
    }

    private static string DisplayName(DirectInputDeviceInfo deviceInfo)
    {
        if (!string.IsNullOrWhiteSpace(deviceInfo.ProductName))
        {
            return deviceInfo.ProductName;
        }

        return string.IsNullOrWhiteSpace(deviceInfo.InstanceName)
            ? deviceInfo.InstanceGuid.ToString()
            : deviceInfo.InstanceName;
    }

    private static int ScaleMagnitude(double intensity) =>
        (int)Math.Round(Math.Clamp(intensity, 0, 1) * DirectInputConstants.DiFfNominalMax);

    private static int ScaleSignedMagnitude(double intensity) =>
        ScaleMagnitude(intensity);

    private static int HertzToPeriod(double frequencyHz)
    {
        var frequency = frequencyHz <= 0 ? 20 : frequencyHz;
        return (int)Math.Clamp(1_000_000 / frequency, 1, int.MaxValue);
    }

    private static int ToDirectInputDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return DirectInputConstants.Infinite;
        }

        return (int)Math.Clamp(duration.TotalMilliseconds * 1000, 1, int.MaxValue);
    }

    // One cached device effect per logical effect. Intensity/frequency/duration
    // are NOT part of the key — they're updated in place on the cached effect via
    // SetParameters, so the device holds only a handful of effects rather than a
    // new one per telemetry frame.
    private static string GetCacheKey(ForceEffect effect) =>
        string.Join('|', effect.Kind, effect.Name, effect.StateKey ?? string.Empty);

    private static void ReleaseEffect(IDirectInputEffect effect)
    {
        _ = effect.Stop();
        _ = effect.Unload();
        _ = Marshal.FinalReleaseComObject(effect);
    }

    private static void FreeIfAllocated(IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    // DirectInput device/effect COM objects have apartment affinity: an object
    // created on a background (MTA) thread cannot be marshalled to the WPF UI
    // (STA) thread and faults with E_NOINTERFACE when used from there (e.g. the
    // Test buttons, or anything created on the UI thread accessing it). The
    // device is created on the UI thread at startup, so all (re)creation and
    // teardown must happen there too. The monitor runs on a background thread for
    // timing/enumeration but routes the actual COM work here.
    private static void InvokeOnDeviceThread(Action action)
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

    private static IntPtr GetMainWindowHandle()
    {
        try
        {
            var window = System.Windows.Application.Current?.MainWindow;
            return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
