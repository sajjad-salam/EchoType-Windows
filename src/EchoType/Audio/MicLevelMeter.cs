using System.Runtime.InteropServices;

namespace EchoType.Audio;

/// <summary>
/// Live microphone level and voice spectrum for the recording HUD. Opens the default
/// capture device in WASAPI shared mode, computes RMS plus a short FFT, and discards the
/// samples — nothing is stored. Shared mode sits alongside ChatGPT/Gemini's own capture.
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

    /// <summary>Spectrum bands shown in the HUD, log-spaced across the voice range.</summary>
    public const int SpectrumBands = 32;
    private const int FftSize = 2048;
    private const float LowHz = 90f;
    private const float HighHz = 7000f;
    private const long SpectrumEveryMs = 30;
    private const long SpectrumStaleMs = 250;

    private readonly object _gate = new();
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _fftRe = new float[FftSize];
    private readonly float[] _fftIm = new float[FftSize];
    private readonly float[] _window = HannWindow(FftSize);
    private readonly float[] _spectrum = new float[SpectrumBands];
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private float _level;
    private float _webLevel;
    private int _ringPos;
    private int _ringFilled;
    private int _sampleRate = 48000;
    private long _spectrumAt;
    private float[]? _floatScratch;
    private short[]? _shortScratch;
    private int[]? _intScratch;

    /// <summary>Smoothed 0–1 microphone energy, safe to read from the UI thread.</summary>
    public float Level {
        get {
            lock (_gate) {
                return Math.Max(_level, _webLevel);
            }
        }
    }

    /// <summary>
    /// Copies the current voice spectrum (0–1 per band, low to high frequency) into
    /// <paramref name="dest"/>, resampled to its length. False when no fresh audio has been
    /// analysed (meter not running, or only the endpoint-peak fallback is available).
    /// </summary>
    public bool CopySpectrum(float[] dest) {
        if (dest.Length == 0) {
            return false;
        }
        lock (_gate) {
            if (_spectrumAt == 0 || Environment.TickCount64 - _spectrumAt > SpectrumStaleMs) {
                return false;
            }
            for (int i = 0; i < dest.Length; i++) {
                float pos = dest.Length == 1 ? 0 : i * (SpectrumBands - 1) / (float)(dest.Length - 1);
                int a = (int)pos;
                int b = Math.Min(SpectrumBands - 1, a + 1);
                float t = pos - a;
                dest[i] = _spectrum[a] + (_spectrum[b] - _spectrum[a]) * t;
            }
            return true;
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
            _spectrumAt = 0;
            Array.Clear(_spectrum);
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
            int rate = Marshal.ReadInt32(mixFormat, 4);
            _sampleRate = rate is >= 8000 and <= 384000 ? rate : 48000;
            _ringPos = 0;
            _ringFilled = 0;

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
                    PushSamples(data, got, channels, bits, ieeeFloat);
                } else if (got > 0) {
                    PushSilence(got);
                }
            } finally {
                capture.ReleaseBuffer(got);
            }
            if (capture.GetNextPacketSize(out frames) != 0) {
                break;
            }
        }
        SetLevel(Shape(peak));
        UpdateSpectrum();
    }

    /// <summary>Mixes a capture packet to mono and appends it to the FFT ring buffer.</summary>
    private void PushSamples(IntPtr data, int frames, int channels, int bits, bool ieeeFloat) {
        int count = frames * channels;
        if (ieeeFloat && bits == 32) {
            EnsureScratch(ref _floatScratch, count);
            Marshal.Copy(data, _floatScratch!, 0, count);
        } else if (!ieeeFloat && bits == 16) {
            EnsureScratch(ref _shortScratch, count);
            Marshal.Copy(data, _shortScratch!, 0, count);
        } else if (!ieeeFloat && bits == 32) {
            EnsureScratch(ref _intScratch, count);
            Marshal.Copy(data, _intScratch!, 0, count);
        } else {
            return;
        }
        for (int i = 0; i < frames; i++) {
            float sum = 0;
            for (int c = 0; c < channels; c++) {
                int index = i * channels + c;
                sum += ieeeFloat ? _floatScratch![index]
                    : bits == 16 ? _shortScratch![index] / 32768f
                    : _intScratch![index] / 2147483648f;
            }
            _ring[_ringPos] = sum / channels;
            _ringPos = (_ringPos + 1) % FftSize;
        }
        _ringFilled = Math.Min(FftSize, _ringFilled + frames);
    }

    private static void EnsureScratch<T>(ref T[]? buffer, int count) {
        if (buffer == null || buffer.Length < count) {
            buffer = new T[Math.Max(count, 4096)];
        }
    }

    private void PushSilence(int frames) {
        for (int i = 0; i < Math.Min(frames, FftSize); i++) {
            _ring[_ringPos] = 0;
            _ringPos = (_ringPos + 1) % FftSize;
        }
        _ringFilled = Math.Min(FftSize, _ringFilled + frames);
    }

    /// <summary>
    /// Hann-windowed FFT over the latest samples, folded into log-spaced bands so the HUD
    /// bars follow the pitch and timbre of the voice instead of just its loudness.
    /// Runs on the capture thread; only the finished bands are shared under the lock.
    /// </summary>
    private void UpdateSpectrum() {
        long now = Environment.TickCount64;
        long last;
        float level;
        lock (_gate) {
            last = _spectrumAt;
            level = _level;
        }
        if (_ringFilled < FftSize / 2 || now - last < SpectrumEveryMs) {
            return;
        }
        for (int i = 0; i < FftSize; i++) {
            _fftRe[i] = _ring[(_ringPos + i) % FftSize] * _window[i];
            _fftIm[i] = 0;
        }
        Fft(_fftRe, _fftIm);

        float binHz = _sampleRate / (float)FftSize;
        float ratio = HighHz / LowHz;
        Span<float> bands = stackalloc float[SpectrumBands];
        for (int b = 0; b < SpectrumBands; b++) {
            float lo = LowHz * MathF.Pow(ratio, b / (float)SpectrumBands);
            float hi = LowHz * MathF.Pow(ratio, (b + 1) / (float)SpectrumBands);
            int first = Math.Clamp((int)(lo / binHz), 1, FftSize / 2 - 1);
            int end = Math.Clamp((int)MathF.Ceiling(hi / binHz), first + 1, FftSize / 2);
            float power = 0;
            for (int k = first; k < end; k++) {
                power = Math.Max(power, _fftRe[k] * _fftRe[k] + _fftIm[k] * _fftIm[k]);
            }
            // Amplitude relative to a full-scale sine through the Hann window (gain 0.5).
            float amplitude = MathF.Sqrt(power) / (FftSize * 0.25f);
            float db = 20f * MathF.Log10(amplitude + 1e-9f);
            // Speech loses energy toward the top; tilt so consonants still show.
            db += 4.5f * MathF.Log2(MathF.Sqrt(lo * hi) / 250f);
            float value = Math.Clamp((db + 72f) / 46f, 0f, 1f);
            // Below the speech gate room noise should not dance.
            bands[b] = level <= 0.001f ? 0 : MathF.Pow(value, 1.35f);
        }

        lock (_gate) {
            if (_cts == null) {
                return; // stopped while computing
            }
            for (int b = 0; b < SpectrumBands; b++) {
                float v = bands[b];
                float old = _spectrum[b];
                _spectrum[b] = v > old ? old * 0.35f + v * 0.65f : old * 0.72f + v * 0.28f;
            }
            _spectrumAt = now;
        }
    }

    /// <summary>In-place iterative radix-2 FFT; length must be a power of two.</summary>
    private static void Fft(float[] re, float[] im) {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++) {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) {
                j ^= bit;
            }
            j ^= bit;
            if (i < j) {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1) {
            float angle = -2f * MathF.PI / len;
            float wRe = MathF.Cos(angle);
            float wIm = MathF.Sin(angle);
            int half = len >> 1;
            for (int i = 0; i < n; i += len) {
                float curRe = 1;
                float curIm = 0;
                for (int k = 0; k < half; k++) {
                    int a = i + k;
                    int b = a + half;
                    float tRe = re[b] * curRe - im[b] * curIm;
                    float tIm = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - tRe;
                    im[b] = im[a] - tIm;
                    re[a] += tRe;
                    im[a] += tIm;
                    float next = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = next;
                }
            }
        }
    }

    private static float[] HannWindow(int n) {
        var w = new float[n];
        for (int i = 0; i < n; i++) {
            w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / (n - 1));
        }
        return w;
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
