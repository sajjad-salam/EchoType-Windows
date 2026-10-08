using Microsoft.Win32;

namespace EchoType;

/// <summary>
/// Launch EchoType when the user signs in to Windows, via the per-user
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run key (no admin rights needed).
/// </summary>
internal static class StartupRegistration {

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "EchoType";

    private static string Command => "\"" + Environment.ProcessPath + "\"";

    /// <summary>Adds or removes the Run entry. Returns false if the registry write failed.</summary>
    public static bool Apply(bool enabled) {
        try {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled) {
                // Rewritten every launch so the entry follows the exe if it was moved.
                if (!string.Equals(key.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase)) {
                    key.SetValue(ValueName, Command, RegistryValueKind.String);
                }
            } else if (key.GetValue(ValueName) is not null) {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        } catch (Exception ex) {
            Log.Write("startup: failed to update Run key: " + ex.Message);
            return false;
        }
    }
}
