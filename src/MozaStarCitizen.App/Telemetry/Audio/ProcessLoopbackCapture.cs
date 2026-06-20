using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MozaStarCitizen.App.Telemetry.Audio;

/// <summary>
/// WASAPI per-process loopback capture (Windows 10 2004+ / build 19041+).
/// Captures only the audio rendered by a target process tree (e.g.
/// StarCitizen.exe), so music, browser, Discord, system sounds, etc. never reach
/// the analyzer. Exposes the same surface as NAudio's WasapiLoopbackCapture
/// (<see cref="IWaveIn"/>) so it drops into the audio source interchangeably.
///
/// Activation must run on an MTA thread, so the whole capture lives on a
/// dedicated background thread.
/// </summary>
public sealed class ProcessLoopbackCapture : IWaveIn
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const int AudclntShareModeShared = 0;
    private const int AudclntStreamflagsLoopback = 0x00020000;
    private const int AudclntStreamflagsEventcallback = 0x00040000;
    private const int AudclntStreamflagsAutoconvertpcm = unchecked((int)0x80000000);
    private const int AudclntStreamflagsSrcDefaultQuality = 0x08000000;
    private const int ProcessLoopbackModeIncludeTargetProcessTree = 0;
    private const int ActivationTypeProcessLoopback = 1;
    private const uint AudclntBufferflagsSilent = 0x2;

    private static Guid _iidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static Guid _iidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly uint _targetProcessId;
    private readonly WaveFormat _waveFormat;
    private IAudioClient? _audioClient;
    private IAudioCaptureClient? _captureClient;
    private Thread? _captureThread;
    private volatile bool _capturing;

    public ProcessLoopbackCapture(int targetProcessId)
    {
        _targetProcessId = (uint)targetProcessId;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    }

    public WaveFormat WaveFormat
    {
        get => _waveFormat;
        set => throw new NotSupportedException("ProcessLoopbackCapture uses a fixed capture format.");
    }

    public event EventHandler<WaveInEventArgs>? DataAvailable;

    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "ProcessLoopbackCapture"
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void StopRecording()
    {
        _capturing = false;
        _captureThread?.Join(2000);
        _captureThread = null;
    }

    public void Dispose() => StopRecording();

    private void CaptureLoop()
    {
        Exception? error = null;
        try
        {
            ActivateAudioClient();

            var formatPtr = WaveFormat.MarshalToPtr(_waveFormat);
            try
            {
                Marshal.ThrowExceptionForHR(_audioClient!.Initialize(
                    AudclntShareModeShared,
                    AudclntStreamflagsLoopback | AudclntStreamflagsAutoconvertpcm | AudclntStreamflagsSrcDefaultQuality,
                    2_000_000, // 200 ms buffer (100 ns units)
                    0,
                    formatPtr,
                    IntPtr.Zero));
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            var captureGuid = _iidAudioCaptureClient;
            Marshal.ThrowExceptionForHR(_audioClient.GetService(ref captureGuid, out var captureObj));
            _captureClient = (IAudioCaptureClient)captureObj;

            Marshal.ThrowExceptionForHR(_audioClient.Start());

            var bytesPerFrame = _waveFormat.Channels * (_waveFormat.BitsPerSample / 8);
            while (_capturing)
            {
                Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out var packetFrames));
                if (packetFrames == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                while (packetFrames != 0 && _capturing)
                {
                    Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(
                        out var dataPtr, out var framesAvailable, out var flags, out _, out _));

                    var byteCount = (int)(framesAvailable * bytesPerFrame);
                    if (byteCount > 0)
                    {
                        var buffer = new byte[byteCount];
                        if ((flags & AudclntBufferflagsSilent) == 0 && dataPtr != IntPtr.Zero)
                        {
                            Marshal.Copy(dataPtr, buffer, 0, byteCount);
                        }

                        DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, byteCount));
                    }

                    _captureClient.ReleaseBuffer(framesAvailable);
                    Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out packetFrames));
                }
            }

            _ = _audioClient.Stop();
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Cleanup();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(error));
        }
    }

    private void ActivateAudioClient()
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = _targetProcessId,
            ProcessLoopbackMode = ProcessLoopbackModeIncludeTargetProcessTree
        };

        var paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);

            var prop = new PropVariantBlob
            {
                Vt = 65, // VT_BLOB
                BlobSize = Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr
            };

            var iid = _iidAudioClient;
            var handler = new ActivationHandler();
            ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, ref prop, handler, out var op);

            if (!handler.Completed.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Process-loopback audio activation timed out.");
            }

            Marshal.ThrowExceptionForHR(op.GetActivateResult(out var activateHr, out var clientObj));
            Marshal.ThrowExceptionForHR(activateHr);
            _audioClient = (IAudioClient)clientObj;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private void Cleanup()
    {
        if (_captureClient is not null)
        {
            try { Marshal.ReleaseComObject(_captureClient); } catch { /* ignore */ }
            _captureClient = null;
        }

        if (_audioClient is not null)
        {
            try { _ = _audioClient.Stop(); } catch { /* ignore */ }
            try { Marshal.ReleaseComObject(_audioClient); } catch { /* ignore */ }
            _audioClient = null;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] ref PropVariantBlob activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Completed = new(false);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Completed.Set();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    // PROPVARIANT laid out for a VT_BLOB payload on x64 (24 bytes: 8-byte header,
    // BLOB.cbSize at +8, BLOB.pBlobData pointer at +16).
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public int BlobSize;
        public int Padding;
        public IntPtr BlobData;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint currentPadding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr dataBuffer, out uint numFramesToRead, out uint bufferFlags, out long devicePosition, out long qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
