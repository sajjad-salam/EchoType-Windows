using EchoType.Web;

namespace EchoType;

internal static class Program {

    /// <summary>Passed by <see cref="Restart"/> so the new process waits for the old one to exit.</summary>
    private const string RestartArg = "--restarted";

    [STAThread]
    private static void Main(string[] args) {
        ApplicationConfiguration.Initialize();
        try {
            Application.SetColorMode(SystemColorMode.Dark);
        } catch (MissingMethodException) {
            // WinForms dark mode arrived in .NET 9.
        }
        WebView2RuntimeLocator.EnsureNativeLoader();

        // Catch UI-thread exceptions so a bad page interaction can't kill the tray app.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("unhandled: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);

        using var mutex = new Mutex(initiallyOwned: true, @"Local\EchoType.SingleInstance", out bool createdNew);
        if (!createdNew && args.Contains(RestartArg)) {
            // Language switch: the previous instance is shutting down; take over once it has.
            try {
                createdNew = mutex.WaitOne(TimeSpan.FromSeconds(15));
            } catch (AbandonedMutexException) {
                createdNew = true;
            }
        }
        if (!createdNew) {
            MessageBox.Show("EchoType is already running.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _ = Task.Run(Sounds.Preload);
        Application.Run(new TrayAppContext());
    }

    /// <summary>
    /// Starts a fresh EchoType that waits for this one to exit. The caller then exits
    /// this instance (used after switching the interface language).
    /// </summary>
    public static bool Restart() {
        try {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) {
                return false;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                FileName = exe,
                Arguments = RestartArg,
                UseShellExecute = false,
            });
            return true;
        } catch (Exception ex) {
            Log.Write("restart failed: " + ex.Message);
            return false;
        }
    }
}
