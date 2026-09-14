namespace EchoType;

/// <summary>File logger (Log.swift analog).</summary>
internal static class Log {
    private static readonly object Gate = new();

    private static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoType", "Logs");

    public static string FilePath { get; } = System.IO.Path.Combine(Directory, "EchoType.log");

    public static void Write(string message) {
        try {
            lock (Gate) {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        } catch {
            // Logging must never take the app down.
        }
    }
}
