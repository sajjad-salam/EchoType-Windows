namespace EchoType;

internal static class Program {

    [STAThread]
    private static void Main() {
        ApplicationConfiguration.Initialize();

        // Catch UI-thread exceptions so a bad page interaction can't kill the tray app.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("unhandled: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);

        using var mutex = new Mutex(initiallyOwned: true, @"Local\EchoType.SingleInstance", out bool createdNew);
        if (!createdNew) {
            MessageBox.Show("EchoType is already running.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());
    }
}
