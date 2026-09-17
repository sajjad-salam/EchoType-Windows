using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace EchoType.Web;

/// <summary>
/// Locates WebView2Loader.dll (the SDK native stub) and, if Evergreen is missing,
/// a Fixed Version runtime folder (msedgewebview2.exe).
/// </summary>
internal static class WebView2RuntimeLocator {

    private static int _resolverSet;

    internal static void EnsureNativeLoader() {
        if (Interlocked.Exchange(ref _resolverSet, 1) != 0) {
            return;
        }
        try {
            NativeLibrary.SetDllImportResolver(
                typeof(CoreWebView2Environment).Assembly, ResolveLoader);
        } catch (InvalidOperationException) {
            // Another resolver already owns this assembly.
        }
    }

    internal static string? FindBrowserExecutableFolder() {
        foreach (var root in CandidateRoots()) {
            var found = FindInRoot(root);
            if (found != null) {
                Log.Write("webview: found fixed runtime at " + found);
                return found;
            }
        }
        return null;
    }

    private static IntPtr ResolveLoader(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
        if (libraryName.IndexOf("WebView2Loader", StringComparison.OrdinalIgnoreCase) < 0) {
            return IntPtr.Zero;
        }
        foreach (var path in LoaderCandidates()) {
            if (NativeLibrary.TryLoad(path, out var handle)) {
                Log.Write("webview: loaded native loader from " + path);
                return handle;
            }
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> LoaderCandidates() {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "WebView2Loader.dll");
        yield return Path.Combine(baseDir, "runtimes", "win-x64", "native", "WebView2Loader.dll");
    }

    private static IEnumerable<string> CandidateRoots() {
        yield return Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoType", "WebView2Runtime");
    }

    private static string? FindInRoot(string root) {
        if (HasRuntime(root)) {
            return Path.GetFullPath(root);
        }
        if (!Directory.Exists(root)) {
            return null;
        }
        try {
            foreach (var dir in Directory.EnumerateDirectories(root)) {
                if (HasRuntime(dir)) {
                    return Path.GetFullPath(dir);
                }
            }
        } catch (Exception ex) {
            Log.Write("webview: runtime probe failed at " + root + ": " + ex.Message);
        }
        return null;
    }

    private static bool HasRuntime(string folder) =>
        File.Exists(Path.Combine(folder, "msedgewebview2.exe"));
}
