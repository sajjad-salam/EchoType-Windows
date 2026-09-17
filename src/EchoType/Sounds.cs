namespace EchoType;

/// <summary>Feedback sounds — Windows stand-ins for the macOS Pop / Tink / Basso.</summary>
internal static class Sounds {
    private static System.Media.SoundPlayer? _cutoffPlayer;

    public static void Start() => System.Media.SystemSounds.Asterisk.Play();
    public static void Pasted() => System.Media.SystemSounds.Beep.Play();
    public static void Error() => System.Media.SystemSounds.Exclamation.Play();

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
