namespace EchoType;

/// <summary>Feedback sounds — Windows stand-ins for the macOS Pop / Tink / Basso.</summary>
internal static class Sounds {
    public static void Start() => System.Media.SystemSounds.Asterisk.Play();
    public static void Pasted() => System.Media.SystemSounds.Beep.Play();
    public static void Error() => System.Media.SystemSounds.Exclamation.Play();
}
