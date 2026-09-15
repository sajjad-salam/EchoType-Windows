namespace EchoType;

/// <summary>File logger (Log.swift analog). Writes to LocalAppData, next to the EXE,
/// and the project root when running from source so failures are easy to find.</summary>
internal static class Log {
    private static readonly object Gate = new();
    private static bool _wroteHeader;

    private static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EchoType", "Logs");

    public static string FilePath { get; } = Path.Combine(AppDataDirectory, "EchoType.log");

    public static string ExeDirectoryFilePath { get; } = Path.Combine(AppContext.BaseDirectory, "EchoType.log");

    public static string? ProjectRootFilePath { get; } = FindProjectRootLogPath();

    public static void Write(string message) {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
        lock (Gate) {
            if (!_wroteHeader) {
                _wroteHeader = true;
                string header =
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} log: appdata={FilePath}{Environment.NewLine}"
                    + $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} log: exe={ExeDirectoryFilePath}{Environment.NewLine}";
                if (ProjectRootFilePath != null) {
                    header += $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} log: project={ProjectRootFilePath}{Environment.NewLine}";
                }
                AppendAll(header);
            }
            AppendAll(line);
        }
    }

    private static void AppendAll(string text) {
        TryAppend(FilePath, AppDataDirectory, text);
        TryAppend(ExeDirectoryFilePath, AppContext.BaseDirectory, text);
        if (ProjectRootFilePath != null) {
            TryAppend(ProjectRootFilePath, Path.GetDirectoryName(ProjectRootFilePath), text);
        }
    }

    private static void TryAppend(string path, string? directory, string text) {
        try {
            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }
            File.AppendAllText(path, text);
        } catch {
            // Logging must never take the app down.
        }
    }

    private static string? FindProjectRootLogPath() {
        try {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null) {
                bool isRoot = File.Exists(Path.Combine(dir.FullName, "EchoType.slnx"))
                    || Directory.Exists(Path.Combine(dir.FullName, ".git"));
                if (isRoot) {
                    return Path.Combine(dir.FullName, "EchoType.log");
                }
                dir = dir.Parent;
            }
        } catch {
            // ignore
        }
        return null;
    }
}
