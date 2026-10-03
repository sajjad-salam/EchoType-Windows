using System.Runtime.InteropServices;

namespace EchoType.Audio;

/// <summary>
/// Live microphone level for the recording HUD. Opens the default capture device
/// in WASAPI shared mode, computes RMS, and discards the samples — nothing is stored.
/// Shared mode sits alongside ChatGPT/Gemini's own capture.
/// </summary>
internal sealed class MicLevelMeter : IDisposable {

    private const int DataFlowCapture = 1;
    private const int RoleMultimedia = 1;
    private const int RoleCommunications = 2;
    private const int ClsCtxAll = 0x17;
    private const int ShareModeShared = 0;
    private const int BufferFlagSilent = 0x2;
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;
    private const uint CoInitMultithreaded = 0;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    private static readonly Guid IidAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private float _level;
    private float _webLevel;

    /// <summary>Smoothed 0–1 microphone energy, safe to read from the UI thread.</summary>
    public float Level {
        get {
            lock (_gate) {
                return Math.Max(_level, _webLevel);
            }
        }
    }

    /// <summary>
    /// Energy from the page's getUserMedia analyser — the same stream ChatGPT/Gemini
    /// is recording. Preferred over WASAPI when both are present.
    /// </summary>
    public void SetWebLevel(float value) {
        lock (_gate) {
            _webLevel = Math.Clamp(value, 0, 1);
        }
    }

    public void Start() {
        lock (_gate) {
            if (_thread is { IsAlive: true }) {
                return;
            }
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var cancel = _cts.Token;
            _thread = new Thread(() => Run(cancel)) {
                IsBackground = true,
                Name = "EchoType.MicMeter",
            };
            _thread.Start();
        }
    }

    public void Stop() {
        CancellationTokenSource? cts;
        Thread? thread;
        lock (_gate) {
            cts = _cts;
            thread = _thread;
            _cts = null;
            _thread = null;
            _level = 0;
            _webLevel = 0;
        }
        try {
            cts?.Cancel();
        } catch (ObjectDisposedException) {
            // already gone
        }
        if (thread != null && thread.ManagedThreadId != Environment.CurrentManagedThreadId) {
            if (!thread.Join(400)) {
                Log.Write("audio: mic meter thread did not stop promptly");
            }
        }
        cts?.Dispose();
    }

    public void Dispose() => Stop();

    private void Run(CancellationToken cancel) {
        CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        try {
            if (!TryCaptureLoop(cancel)) {
                MeterLoop(cancel);
            }
        } catch (Exception ex) {
            Log.Write("audio: mic meter failed: " + ex.Message);
        } finally {
            SetLevel(0);
            CoUninitialize();
        }
    }

    private bool TryCaptureLoop(CancellationToken cancel) {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr mixFormat = IntPtr.Zero;
        try {
            if (enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleCommunications, out device) != 0
                || device == null) {
                if (enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleMultimedia, out device) != 0
                    || device == null) {
                    return false;
                }
            }

            Guid iid = IidAudioClient;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? raw) != 0
                || raw is not IAudioClient audioClient) {
                return false;
            }
            client = audioClient;
            if (client.GetMixFormat(out mixFormat) != 0 || mixFormat == IntPtr.Zero) {
                return false;
            }
            if (!TryReadFormat(mixFormat, out int channels, out int bits, out bool ieeeFloat)) {
                return false;
            }

            // 200 ms shared buffer — enough to poll without glitching other capturers.
            long bufferHns = 2_000_000;
            int hr = client.Initialize(ShareModeShared, 0, bufferHns, 0, mixFormat, IntPtr.Zero);
            if (hr != 0) {
                Log.Write("audio: mic meter initialize hr=0x" + hr.ToString("X8"));
                return false;
            }

            Guid capIid = IidAudioCaptureClient;
            if (client.GetService(ref capIid, out object? capRaw) != 0
                || capRaw is not IAudioCaptureClient cap) {
                return false;
            }
            capture = cap;
            if (client.Start() != 0) {
                return false;
            }

            while (!cancel.IsCancellationRequested) {
                Drain(capture, channels, bits, ieeeFloat);
                cancel.WaitHandle.WaitOne(8);
            }
            try { client.Stop(); } catch { /* ignore */ }
            return true;
        } finally {
            if (mixFormat != IntPtr.Zero) {
                Marshal.FreeCoTaskMem(mixFormat);
            }
            Release(capture);
            Release(client);
            Release(device);
            Release(enumerator);
        }
    }

    private void Drain(IAudioCaptureClient capture, int channels, int bits, bool ieeeFloat) {
        if (capture.GetNextPacketSize(out int frames) != 0 || frames <= 0) {
            return;
        }
        float peak = 0;
        while (frames > 0) {
            if (capture.GetBuffer(out IntPtr data, out int got, out int flags, out _, out _) != 0) {
                break;
            }
            try {
                if (got > 0 && (flags & BufferFlagSilent) == 0 && data != IntPtr.Zero) {
                    peak = Math.Max(peak, Rms(data, got, channels, bits, ieeeFloat));
                }
            } finally {
                capture.ReleaseBuffer(got);
            }
            if (capture.GetNextPacketSize(out frames) != 0) {
                break;
            }
        }
        SetLevel(Shape(peak));
    }

    private void MeterLoop(CancellationToken cancel) {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        IAudioMeterInformation? meter = null;
        try {
            if (enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleCommunications, out device) != 0
                || device == null) {
                if (enumerator.GetDefaultAudioEndpoint(DataFlowCapture, RoleMultimedia, out device) != 0
                    || device == null) {
                    return;
                }
            }
            Guid iid = IidAudioMeterInformation;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object? raw) != 0
                || raw is not IAudioMeterInformation info) {
                return;
            }
            meter = info;
            Log.Write("audio: mic meter using endpoint peak fallback");
            while (!cancel.IsCancellationRequested) {
                if (meter.GetPeakValue(out float peak) == 0) {
                    SetLevel(Shape(peak));
                }
                cancel.WaitHandle.WaitOne(16);
            }
        } finally {
            Release(meter);
            Release(device);
            Release(enumerator);
        }
    }

    private void SetLevel(float value) {
        lock (_gate) {
            // Fast attack, slower release so the HUD wave decays naturally.
            _level = value > _level
                ? _level * 0.25f + value * 0.75f
                : _level * 0.82f + value * 0.18f;
        }
    }

    /// <summary>Lift quiet speech above the noise floor without clipping loud talk.</summary>
    private static float Shape(float rms) {
        const float floor = 0.006f;
        if (rms <= floor) {
            return 0;
        }
        float n = Math.Clamp((rms - floor) / 0.22f, 0, 1);
        return MathF.Pow(n, 0.55f);
    }

    private static float Rms(IntPtr data, int frames, int channels, int bits, bool ieeeFloat) {
        if (frames <= 0 || channels <= 0) {
            return 0;
        }
        double sum = 0;
        int count = 0;
        if (ieeeFloat && bits == 32) {
            int stride = channels;
            for (int i = 0; i < frames; i++) {
                float s = Marshal.PtrToStructure<float>(data + (i * stride) * 4);
                sum += s * s;
                count++;
            }
        } else if (!ieeeFloat && bits == 16) {
            int stride = channels;
            for (int i = 0; i < frames; i++) {
                short s = Marshal.ReadInt16(data, (i * stride) * 2);
                float v = s / 32768f;
                sum += v * v;
                count++;
            }
        } else if (!ieeeFloat && bits == 32) {
            int stride = channels;
            for (int i = 0; i < frames; i++) {
                int s = Marshal.ReadInt32(data, (i * stride) * 4);
                float v = s / 2147483648f;
                sum += v * v;
                count++;
            }
        } else {
            return 0;
        }
        return count == 0 ? 0 : (float)Math.Sqrt(sum / count);
    }

    private static bool TryReadFormat(IntPtr format, out int channels, out int bits, out bool ieeeFloat) {
        channels = Marshal.ReadInt16(format, 2);
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        bits = Marshal.ReadInt16(format, 14);
        ieeeFloat = tag == WaveFormatIeeeFloat;
        if (tag == WaveFormatExtensible) {
            var sub = Marshal.PtrToStructure<Guid>(format + 24);
            ieeeFloat = sub == SubtypeIeeeFloat;
            if (!ieeeFloat && sub != SubtypePcm) {
                return false;
            }
        } else if (tag != WaveFormatPcm && tag != WaveFormatIeeeFloat) {
            return false;
        }
        return channels > 0 && bits > 0;
    }

    private static void Release(object? com) {
        if (com == null) {
            return;
        }
        try {
            Marshal.ReleaseComObject(com);
        } catch {
            // Best effort.
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
            long hnsPeriodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out int numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long hnsLatency);
        [PreserveSig] int GetCurrentPadding(out int numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient {
        [PreserveSig] int GetBuffer(out IntPtr data, out int numFramesToRead, out int flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFramesRead);
        [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, [Out] float[] values);
        [PreserveSig] int QueryHardwareSupport(out int mask);
    }
}
