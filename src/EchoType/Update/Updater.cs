using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace EchoType.Update;

internal sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, string? ZipUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer EchoType and installs it: downloads the release zip,
/// then a small PowerShell script waits for EchoType to exit, copies the new files over
/// the old ones and starts EchoType again. Only GitHub is contacted; nothing is sent
/// beyond the HTTP request itself.
/// </summary>
internal static class Updater {

    public const string Repository = "sajjad-salam/EchoType-Windows";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    private const string AssetName = "EchoType-win-x64.zip";

    public static Version Current { get; } =
        typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    internal static HttpClient CreateClient() {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub requires a User-Agent; a generic one, nothing about the user.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EchoType-Windows/" + Current);
        return http;
    }

    /// <summary>True when the last check reached GitHub but the repository has no published release.</summary>
    public static bool NoReleasesYet { get; private set; }

    /// <summary>The latest published release, or null when it can't be read.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancel = default) {
        NoReleasesYet = false;
        try {
            using var http = CreateClient();
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestApi, cancel);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out Version version)) {
                Log.Write("update: release tag \"" + tag + "\" is not a version");
                return null;
            }
            string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
            string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            string? zip = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array) {
                foreach (var asset in assets.EnumerateArray()) {
                    if (string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) {
                        zip = asset.GetProperty("browser_download_url").GetString();
                    }
                }
            }
            return new ReleaseInfo(version, tag, page, zip, notes);
        } catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) {
            NoReleasesYet = true;
            Log.Write("update: no release published yet");
            return null;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Log.Write("update: check failed: " + ex.Message);
            return null;
        }
    }

    public static bool TryParseVersion(string tag, out Version version) {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) {
            t = t[..dash];
        }
        if (Version.TryParse(t, out var parsed)) {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>
    /// Downloads <paramref name="release"/> and starts the installer script. Returns true when
    /// EchoType should now exit so the script can replace its files.
    /// </summary>
    public static async Task<bool> DownloadAndInstallAsync(ReleaseInfo release, IProgress<int>? progress = null) {
        if (release.ZipUrl == null) {
            return false;
        }
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) {
            return false;
        }
        string appDir = Path.GetDirectoryName(exe)!;
        string work = Path.Combine(Path.GetTempPath(), "EchoType", "update-" + release.Version);
        Directory.CreateDirectory(work);
        string zipPath = Path.Combine(work, AssetName);

        using (var http = CreateClient()) {
            http.Timeout = TimeSpan.FromMinutes(10);
            using var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(zipPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0) {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) {
                    progress?.Report((int)(done * 100 / total));
                }
            }
        }
        Log.Write("update: downloaded " + release.Tag + " (" + new FileInfo(zipPath).Length + " bytes)");

        // Validate before handing over: the zip must contain EchoType.exe.
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath)) {
            if (!archive.Entries.Any(e => string.Equals(e.Name, Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))) {
                Log.Write("update: the release zip has no " + Path.GetFileName(exe) + ", not installing");
                return false;
            }
        }

        string script = Path.Combine(work, "install.ps1");
        string log = Path.Combine(work, "install.log");
        File.WriteAllText(script, InstallScript);
        var psi = new ProcessStartInfo {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (string arg in new[] {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ProcessId", Environment.ProcessId.ToString(),
                     "-Zip", zipPath, "-Dest", appDir, "-Exe", exe, "-Log", log,
                 }) {
            psi.ArgumentList.Add(arg);
        }
        Process.Start(psi);
        Log.Write("update: installer started for " + release.Tag + " → " + appDir);
        return true;
    }

    /// <summary>
    /// Waits for EchoType to exit, extracts the release over the install folder and restarts it.
    /// If the folder can't be written (e.g. Program Files without admin rights), it opens the
    /// release page instead so the user can update by hand.
    /// </summary>
    private const string InstallScript = """
        param([int]$ProcessId, [string]$Zip, [string]$Dest, [string]$Exe, [string]$Log)
        $ErrorActionPreference = 'Stop'
        function Note($m) { Add-Content -Path $Log -Value ((Get-Date -Format s) + ' ' + $m) }
        try {
            try { Wait-Process -Id $ProcessId -Timeout 60 } catch { }
            Start-Sleep -Milliseconds 500
            $staging = Join-Path (Split-Path $Zip) 'files'
            if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
            Expand-Archive -Path $Zip -DestinationPath $staging -Force
            Get-ChildItem -Path $staging -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring($staging.Length).TrimStart('\')
                $to = Join-Path $Dest $rel
                New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $to -Force
            }
            Note 'installed'
            Start-Process -FilePath $Exe
        } catch {
            Note ('failed: ' + $_.Exception.Message)
            Start-Process 'https://github.com/sajjad-salam/EchoType-Windows/releases/latest'
            if (Test-Path $Exe) { Start-Process -FilePath $Exe }
        }
        """;
}
