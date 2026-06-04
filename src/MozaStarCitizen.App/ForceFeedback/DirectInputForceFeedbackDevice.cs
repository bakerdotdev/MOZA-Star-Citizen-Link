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
        StopEffect(key);

        var directInputEffect = GetOrCreateEffect(effect);

        AppLog.Write($"DirectInput starting effect '{effect.Name}' on '{_productName}' intensity={effect.Intensity:0.###} durationMs={effect.Duration.TotalMilliseconds:0} frequencyHz={effect.FrequencyHz:0.###}.");
        StartEffect(directInputEffect, effect.Name);

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

        var createdEffect = effect.Kind switch
        {
            ForceEffectKind.Bump or ForceEffectKind.ConstantForce => CreateBumpEffect(effect),
            ForceEffectKind.PeriodicVibration or ForceEffectKind.StateVibration => CreatePeriodicEffect(effect),
            _ => CreatePeriodicEffect(effect)
        };

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

    private IDirectInputEffect CreateBumpEffect(ForceEffect effect)
    {
        try
        {
            return CreateConstantEffect(effect);
        }
        catch (Exception ex)
        {
            AppLog.Write(ex, $"DirectInput constant-force bump creation failed for '{effect.Name}'. Falling back to a short sine pulse.");
            return CreatePeriodicEffect(effect with
            {
                FrequencyHz = effect.FrequencyHz <= 0 ? 42 : effect.FrequencyHz
            });
        }
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

    private IDirectInputEffect CreatePeriodicEffect(ForceEffect effect)
    {
        var periodic = new DirectInputPeriodic
        {
            Magnitude = ScaleMagnitude(effect.Intensity),
            Offset = 0,
            Phase = 0,
            Period = HertzToPeriod(effect.FrequencyHz)
        };

        return CreateEffect(
            DirectInputConstants.GuidSine,
            effect,
            Marshal.SizeOf<DirectInputPeriodic>(),
            pointer => Marshal.StructureToPtr(periodic, pointer, false));
    }

    private IDirectInputEffect CreateConstantEffect(ForceEffect effect)
    {
        var constant = new DirectInputConstantForce
        {
            Magnitude = ScaleSignedMagnitude(effect.Intensity)
        };

        return CreateEffect(
            DirectInputConstants.GuidConstantForce,
            effect,
            Marshal.SizeOf<DirectInputConstantForce>(),
            pointer => Marshal.StructureToPtr(constant, pointer, false));
    }

    private IDirectInputEffect CreateEffect(
        Guid effectGuid,
        ForceEffect effect,
        int typeSpecificSize,
        Action<IntPtr> writeTypeSpecificParameters)
    {
        EnsureInitialized();

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
            writeTypeSpecificParameters(typeSpecific);

            var directInputEffect = new DirectInputEffect
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

            var result = _device!.CreateEffect(ref effectGuid, ref directInputEffect, out var createdEffect, IntPtr.Zero);
            if (result == DirectInputConstants.DierrNotExclusiveAcquired)
            {
                AppLog.Write($"DirectInput lost exclusive acquisition for '{_productName}' while creating '{effect.Name}'. Re-acquiring and retrying once.");
                Reacquire();
                result = _device.CreateEffect(ref effectGuid, ref directInputEffect, out createdEffect, IntPtr.Zero);
            }

            DirectInputNative.ThrowIfFailed(result, $"DirectInput could not create '{effect.Name}'");
            DownloadEffect(createdEffect, effect.Name);
            return createdEffect;
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

    private void StartEffect(IDirectInputEffect effect, string effectName)
    {
        DownloadEffect(effect, effectName);

        var startResult = effect.Start(1, 0);
        if (startResult == DirectInputConstants.DierrNotDownloaded)
        {
            AppLog.Write($"DirectInput effect '{effectName}' was not downloaded. Downloading and retrying start once.");
            DownloadEffect(effect, effectName);
            startResult = effect.Start(1, 0);
        }
        else if (startResult == DirectInputConstants.DierrNotExclusiveAcquired)
        {
            AppLog.Write($"DirectInput lost exclusive acquisition for '{_productName}' while starting '{effectName}'. Re-acquiring and retrying once.");
            Reacquire();
            DownloadEffect(effect, effectName);
            startResult = effect.Start(1, 0);
        }
        else if (startResult == DirectInputConstants.DierrEffectPlaying)
        {
            _ = effect.Stop();
            startResult = effect.Start(1, 0);
        }

        DirectInputNative.ThrowIfFailed(startResult, $"DirectInput could not start '{effectName}'");
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

    private static string GetCacheKey(ForceEffect effect) =>
        string.Join(
            '|',
            effect.Kind,
            effect.Name,
            Math.Round(Math.Clamp(effect.Intensity, 0, 1), 3),
            (int)Math.Clamp(effect.Duration.TotalMilliseconds, 0, int.MaxValue),
            Math.Round(effect.FrequencyHz, 3),
            effect.StateKey ?? string.Empty);

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
