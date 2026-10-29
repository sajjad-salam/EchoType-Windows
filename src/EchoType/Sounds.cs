namespace EchoType;

/// <summary>
/// Feedback sounds. Start and done are the start and stop clicks of a
/// stopwatch ("stopwatch start stop, no ticks" by Nicholas Judy), embedded
/// from Assets/Sounds. If a resource is missing, a soft
/// synthesised bubble pop plays instead. Dropping a <c>start.wav</c> or
/// <c>done.wav</c> into <c>%APPDATA%\EchoType\Sounds</c> replaces the matching sound.
/// </summary>
internal static class Sounds {
    private const int ChimeSampleRate = 44100;

    private static string CustomDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoType", "Sounds");

    private static readonly Lazy<byte[]?> StartWav = new(() => TryLoadCustom("start.wav") ?? TryLoadEmbedded("start.wav") ?? TryBuild(
        [(420.0, 760.0, 0, 95, 0.7), (640.0, 1040.0, 70, 120, 1.0)], 380));
    private static readonly Lazy<byte[]?> DoneWav = new(() => TryLoadCustom("done.wav") ?? TryLoadEmbedded("done.wav") ?? TryBuild(
        [(1040.0, 640.0, 0, 95, 0.8), (760.0, 420.0, 70, 140, 1.0)], 400));

    private static System.Media.SoundPlayer? _chimePlayer;
    private static System.Media.SoundPlayer? _cutoffPlayer;

    public static void Start() => PlayChime(StartWav.Value, System.Media.SystemSounds.Asterisk);
    public static void Pasted() => PlayChime(DoneWav.Value, System.Media.SystemSounds.Beep);
    public static void Error() => System.Media.SystemSounds.Exclamation.Play();

    /// <summary>Warms the chime cache so the first hotkey press plays instantly.</summary>
    public static void Preload() {
        _ = StartWav.Value;
        _ = DoneWav.Value;
    }

    private static void PlayChime(byte[]? wav, System.Media.SystemSound fallback) {
        if (wav == null) {
            fallback.Play();
            return;
        }
        try {
            // SoundPlayer plays asynchronously, so keep it referenced until the next chime.
            _chimePlayer?.Stop();
            _chimePlayer?.Dispose();
            _chimePlayer = new System.Media.SoundPlayer(new MemoryStream(wav, writable: false));
            _chimePlayer.Play();
        } catch {
            fallback.Play();
        }
    }

    private static byte[]? TryLoadCustom(string name) {
        string path = Path.Combine(CustomDir, name);
        try {
            if (!File.Exists(path)) {
                return null;
            }
            byte[] wav = File.ReadAllBytes(path);
            Log.Write("sounds: using custom " + path);
            return wav;
        } catch (Exception ex) {
            Log.Write("sounds: custom " + name + " unreadable: " + ex.Message);
            return null;
        }
    }

    private static byte[]? TryLoadEmbedded(string name) {
        try {
            using var stream = typeof(Sounds).Assembly.GetManifestResourceStream("EchoType.Sounds." + name);
            if (stream == null) {
                return null;
            }
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        } catch {
            return null;
        }
    }

    private static byte[]? TryBuild((double FromHz, double ToHz, int StartMs, int DurMs, double Gain)[] bubbles, int totalMs) {
        try {
            return BuildBubbleWav(bubbles, totalMs);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Bubble voice: a near-sine (a whisper of 2nd harmonic for body) whose
    /// pitch glides from FromHz to ToHz, fast at first then settling, which is
    /// what makes a water-drop "bloop". 3 ms attack, quick exponential decay,
    /// a few short echoes for a little room, then peak-normalise to -6 dBFS.
    /// </summary>
    private static byte[] BuildBubbleWav((double FromHz, double ToHz, int StartMs, int DurMs, double Gain)[] bubbles, int totalMs) {
        const int sr = ChimeSampleRate;
        int n = MsToSamples(totalMs, sr);
        var dry = new double[n];
        int attack = MsToSamples(3, sr);
        foreach (var b in bubbles) {
            int start = MsToSamples(b.StartMs, sr);
            double dur = b.DurMs / 1000.0;
            double tau = dur / 3.2;
            double phase = 0;
            for (int i = 0; start + i < n; i++) {
                double t = i / (double)sr;
                double x = Math.Min(1.0, t / dur);
                double hz = b.FromHz * Math.Pow(b.ToHz / b.FromHz, 1 - Math.Pow(1 - x, 2.2));
                phase += 2 * Math.PI * hz / sr;
                double env = (i < attack ? i / (double)attack : 1.0) * Math.Exp(-t / tau);
                dry[start + i] += (Math.Sin(phase) + 0.12 * Math.Sin(2 * phase)) * env * b.Gain;
            }
        }

        var wet = (double[])dry.Clone();
        foreach (var (delayMs, gain) in new[] { (23, 0.18), (37, 0.13), (53, 0.09), (79, 0.06) }) {
            int d = MsToSamples(delayMs, sr);
            for (int i = d; i < n; i++) {
                wet[i] += dry[i - d] * gain;
            }
        }
        int fade = Math.Min(n, MsToSamples(30, sr));
        for (int i = 0; i < fade; i++) {
            wet[n - 1 - i] *= i / (double)fade;
        }
        double peak = 0;
        foreach (double v in wet) {
            peak = Math.Max(peak, Math.Abs(v));
        }
        double scale = peak > 0 ? 0.5 / peak : 0;

        using var ms = new MemoryStream(44 + n * 2);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true)) {
            WriteWavHeader(w, sr, n);
            foreach (double v in wet) {
                w.Write((short)(v * scale * short.MaxValue));
            }
        }
        return ms.ToArray();
    }

    private static void WriteWavHeader(BinaryWriter w, int sampleRate, int samples) {
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples * 2);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(sampleRate);
        w.Write(sampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(samples * 2);
    }

    /// <summary>
    /// Distinct descending two-tone so a mid-sentence cutoff is obvious
    /// even when EchoType is in the tray and the user is looking elsewhere.
    /// </summary>
    public static void RecordingStopped() {
        try {
            var wav = BuildTwoToneWav(940, 90, 45, 620, 170);
            _cutoffPlayer?.Dispose();
            _cutoffPlayer = new System.Media.SoundPlayer(wav);
            _cutoffPlayer.Play();
        } catch {
            Error();
        }
    }

    private static MemoryStream BuildTwoToneWav(
        int hz1, int ms1, int gapMs, int hz2, int ms2) {
        const int sampleRate = 22050;
        int n1 = MsToSamples(ms1, sampleRate);
        int gap = MsToSamples(gapMs, sampleRate);
        int n2 = MsToSamples(ms2, sampleRate);
        int samples = n1 + gap + n2;
        var ms = new MemoryStream(44 + samples * 2);
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true)) {
            WriteWavHeader(w, sampleRate, samples);
            WriteTone(w, hz1, n1, sampleRate);
            for (int i = 0; i < gap; i++) {
                w.Write((short)0);
            }
            WriteTone(w, hz2, n2, sampleRate);
        }
        ms.Position = 0;
        return ms;
    }

    private static int MsToSamples(int ms, int sampleRate) =>
        Math.Max(1, (int)(sampleRate * (ms / 1000.0)));

    private static void WriteTone(BinaryWriter w, int hz, int samples, int sampleRate) {
        int fade = Math.Min(samples / 6, sampleRate / 80);
        for (int i = 0; i < samples; i++) {
            double env = 1.0;
            if (i < fade) {
                env = i / (double)fade;
            } else if (i > samples - fade) {
                env = (samples - i) / (double)fade;
            }
            double s = Math.Sin(2 * Math.PI * hz * i / sampleRate) * env * 0.42;
            w.Write((short)(s * short.MaxValue));
        }
    }
}
