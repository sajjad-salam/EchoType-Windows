using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using EchoType.Web;

namespace EchoType.Health;

internal enum MicAccess {
    Allowed,
    /// <summary>Settings → Privacy &amp; security → Microphone → "Microphone access" is off.</summary>
    DeniedForAll,
    /// <summary>"Let desktop apps access your microphone" is off.</summary>
    DeniedForDesktopApps,
    /// <summary>The device-wide policy (an administrator or the device owner) turned it off.</summary>
    DeniedByPolicy,
    Unknown,
}

/// <summary>Windows-side prerequisites the Health card and setup wizard check.</summary>
internal static class SystemChecks {

    private const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public static MicAccess Microphone() {
        try {
            if (Read(Registry.LocalMachine, ConsentKey) == "Deny") {
                return MicAccess.DeniedByPolicy;
            }
            string? user = Read(Registry.CurrentUser, ConsentKey);
            if (user == "Deny") {
                return MicAccess.DeniedForAll;
            }
            string? desktop = Read(Registry.CurrentUser, ConsentKey + @"\NonPackaged");
            if (desktop == "Deny") {
                return MicAccess.DeniedForDesktopApps;
            }
            return user == null && desktop == null ? MicAccess.Unknown : MicAccess.Allowed;
        } catch (Exception ex) {
            Log.Write("health: microphone consent check failed: " + ex.Message);
            return MicAccess.Unknown;
        }
    }

    private static string? Read(RegistryKey root, string path) {
        using var key = root.OpenSubKey(path);
        return key?.GetValue("Value") as string;
    }

    private static bool? _webView2;

    /// <summary>True when an Evergreen or fixed WebView2 runtime is available (cached).</summary>
    public static bool WebView2Installed() {
        if (_webView2 is bool known) {
            return known;
        }
        try {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
            _webView2 = true;
        } catch {
            string? folder = WebView2RuntimeLocator.FindBrowserExecutableFolder();
            try {
                _webView2 = folder != null && CoreWebView2Environment.GetAvailableBrowserVersionString(folder) != null;
            } catch {
                _webView2 = false;
            }
        }
        return _webView2.Value;
    }

    public static void OpenMicrophoneSettings() => OpenUri("ms-settings:privacy-microphone");

    public static void OpenUri(string uri) {
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = uri, UseShellExecute = true });
        } catch (Exception ex) {
            Log.Write("open " + uri + " failed: " + ex.Message);
        }
    }
}
