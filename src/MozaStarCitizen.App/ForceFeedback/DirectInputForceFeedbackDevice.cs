using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MozaStarCitizen.App.Diagnostics;
using MozaStarCitizen.App.ForceFeedback.DirectInput;
using MozaStarCitizen.App.Models;

namespace MozaStarCitizen.App.ForceFeedback;

public sealed class DirectInputForceFeedbackDevice : IForceFeedbackDevice
{
    private readonly Guid _instanceGuid;
    private readonly string _productName;
    private readonly Dictionary<string, IDirectInputEffect> _activeEffects = [];
    private readonly Dictionary<string, IDirectInputEffect> _effectCache = [];
    private readonly object _sync = new();
    private IDirectInput8W? _directInput;
    private IDirectInputDevice8W? _device;

    private DirectInputForceFeedbackDevice(DirectInputDeviceInfo deviceInfo)
    {
        _instanceGuid = deviceInfo.InstanceGuid;
        _productName = string.IsNullOrWhiteSpace(deviceInfo.ProductName)
            ? deviceInfo.InstanceName
            : deviceInfo.ProductName;
    }

    public string Name => $"DirectInput: {_productName}";

    public string Status => "Using Windows DirectInput force feedback.";

    public static IForceFeedbackDevice? CreateIfAvailable()
    {
        try
        {
            var devices = DirectInputNative.EnumerateForceFeedbackDevices();
            AppLog.Write($"DirectInput force-feedback enumeration found {devices.Count} device(s): {string.Join("; ", devices.Select(DisplayName))}");
            var selected = devices
                .OrderByDescending(IsPreferredDevice)
                .ThenBy(d => d.ProductName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            return selected is null ? null : new DirectInputForceFeedbackDevice(selected);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, "DirectInput force-feedback enumeration failed");
            return null;
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_device is not null)
        {
            return Task.CompletedTask;
        }

        _directInput = DirectInputNative.CreateDirectInput();
        var guid = _instanceGuid;
        DirectInputNative.ThrowIfFailed(
            _directInput.CreateDevice(ref guid, out _device, IntPtr.Zero),
            $"DirectInput could not open '{_productName}'");

        DirectInputNative.SetTwoAxisJoystickDataFormat(_device, _productName);
        AppLog.Write($"DirectInput data format set for '{_productName}'.");

        var cooperativeFlags = DirectInputConstants.DiExclusive | DirectInputConstants.DiBackground;
        var cooperativeResult = _device.SetCooperativeLevel(GetMainWindowHandle(), cooperativeFlags);
        if (!DirectInputNative.Succeeded(cooperativeResult))
        {
            // Some drivers reject a null HWND for background exclusive mode. Effect creation may still work.
            AppLog.Write($"DirectInput SetCooperativeLevel returned 0x{cooperativeResult:X8} for '{_productName}'.");
        }

        DirectInputNative.ThrowIfFailed(_device.Acquire(), $"DirectInput could not acquire '{_productName}'");
        _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcReset);
        _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcSetActuatorsOn);
        AppLog.Write($"DirectInput initialized '{_productName}'.");

        return Task.CompletedTask;
    }

    public Task PrepareAsync(IEnumerable<ForceEffect> effects, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        foreach (var effect in effects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = GetOrCreateEffect(effect);
        }

        AppLog.Write($"DirectInput prepared {_effectCache.Count} cached effect(s) for '{_productName}'.");
        return Task.CompletedTask;
    }

    public Task PlayAsync(ForceEffect effect, CancellationToken cancellationToken)
    {
        EnsureInitialized();

        var key = effect.StateKey ?? $"transient-{Guid.NewGuid():N}";

        bool alreadyRunning;
        lock (_sync)
        {
            alreadyRunning = _activeEffects.ContainsKey(key);
        }

        // One DirectInput effect per logical state (engine, atmosphere, impact,
        // ...), updated in place. Creating a fresh effect every telemetry frame
        // (the old behaviour) leaked downloaded effects until the device ran out
        // of slots and CreateEffect faulted natively.
        var directInputEffect = GetOrCreateEffect(effect);

        AppLog.Write($"DirectInput effect '{effect.Name}' on '{_productName}' intensity={effect.Intensity:0.###} durationMs={effect.Duration.TotalMilliseconds:0} frequencyHz={effect.FrequencyHz:0.###}.");
        ApplyParameters(directInputEffect, effect, start: !alreadyRunning);

        lock (_sync)
        {
            _activeEffects[key] = directInputEffect;
        }

        if (effect.StateKey is null && effect.Duration > TimeSpan.Zero)
        {
            _ = StopTransientAsync(key, effect.Duration);
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
        List<string> keys;
        lock (_sync)
        {
            keys = _activeEffects.Keys.ToList();
        }

        foreach (var key in keys)
        {
            StopEffect(key);
        }

        if (_device is not null)
        {
            _ = _device.SendForceFeedbackCommand(DirectInputConstants.DisffcStopAll);
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
            if (result == DirectInputConstants.DierrNotExclusiveAcquired)
            {
                Reacquire();
                result = existing.SetParameters(ref dieffect, flags);
            }
            else if (result == DirectInputConstants.DierrNotDownloaded)
            {
                DownloadEffect(existing, effect.Name);
                result = existing.SetParameters(ref dieffect, flags);
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
        IDirectInputEffect? effect;
        lock (_sync)
        {
            if (!_activeEffects.Remove(key, out effect))
            {
                return;
            }
        }

        _ = effect.Stop();
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

    private static IntPtr GetMainWindowHandle()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        return window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
    }
}
