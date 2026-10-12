using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoType.Input;

/// <summary>
/// Finds image files referenced by selected text so Ask model can attach them: absolute
/// paths, file:/// URIs, Markdown <c>![alt](path)</c> and Obsidian <c>![[name.png]]</c>
/// embeds. Relative paths and bare file names are looked up in the user's Obsidian vaults.
/// References that do not resolve to an existing image stay in the text.
/// </summary>
internal static class ImageReferences {

    public const int MaxImages = 4;
    private const long MaxBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    private static readonly Regex WikiEmbed = new(@"!?\[\[([^\]\|#]+)(?:[#\|][^\]]*)?\]\]", RegexOptions.Compiled);
    private static readonly Regex MarkdownImage = new(@"!\[[^\]]*\]\(\s*<?([^)>]+?)>?(?:\s+""[^""]*"")?\s*\)", RegexOptions.Compiled);

    internal sealed record Result(IReadOnlyList<string> Paths, string Remainder);

    public static Result Extract(string selection) {
        var paths = new List<string>();
        string text = selection;

        text = WikiEmbed.Replace(text, m => Take(m.Groups[1].Value, paths) ? "" : m.Value);
        text = MarkdownImage.Replace(text, m => Take(m.Groups[1].Value, paths) ? "" : m.Value);

        // A line that is just a path (Explorer's "Copy as path", a typed link, …).
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++) {
            if (Take(lines[i], paths)) {
                lines[i] = "";
            }
        }
        string remainder = string.Join('\n', lines).Trim();
        return new Result(paths, paths.Count == 0 ? selection : remainder);
    }

    private static bool Take(string reference, List<string> paths) {
        if (paths.Count >= MaxImages) {
            return false;
        }
        string? path = Resolve(reference);
        if (path == null) {
            return false;
        }
        if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) {
            paths.Add(path);
        }
        return true;
    }

    private static string? Resolve(string reference) {
        string r = reference.Trim().Trim('"', '\'', '<', '>', '`').Trim();
        if (r.Length == 0 || r.Length > 1024 || r.Contains('\n')) {
            return null;
        }
        if (r.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(r, UriKind.Absolute, out var uri) && uri.IsFile) {
            r = uri.LocalPath;
        } else if (r.Contains('%')) {
            try { r = Uri.UnescapeDataString(r); } catch { /* keep as typed */ }
        }
        if (r.Contains("://", StringComparison.Ordinal)) {
            return null;
        }
        r = r.Replace('/', Path.DirectorySeparatorChar);
        string ext;
        try {
            ext = Path.GetExtension(r);
        } catch (ArgumentException) {
            return null;
        }
        if (!Extensions.Contains(ext)) {
            return null;
        }
        try {
            if (Path.IsPathFullyQualified(r)) {
                return Usable(r) ? Path.GetFullPath(r) : null;
            }
            return FindInObsidianVaults(r);
        } catch (Exception ex) {
            Log.Write("image-refs: could not resolve \"" + r + "\": " + ex.Message);
            return null;
        }
    }

    private static bool Usable(string path) {
        try {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 && info.Length <= MaxBytes;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Relative path or bare file name: try each vault root, then search the vault for the
    /// file name (Obsidian embeds only name the file; the attachment folder varies).
    /// </summary>
    private static string? FindInObsidianVaults(string relative) {
        string name = Path.GetFileName(relative);
        if (name.Length == 0) {
            return null;
        }
        var vaults = ObsidianVaults();
        foreach (string vault in vaults) {
            string candidate = Path.GetFullPath(Path.Combine(vault, relative.TrimStart(Path.DirectorySeparatorChar)));
            if (candidate.StartsWith(vault, StringComparison.OrdinalIgnoreCase) && Usable(candidate)) {
                return candidate;
            }
        }
        var options = new EnumerationOptions {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };
        foreach (string vault in vaults) {
            string? hit = Directory.EnumerateFiles(vault, name, options).FirstOrDefault(Usable);
            if (hit != null) {
                Log.Write("image-refs: found \"" + name + "\" in vault " + vault);
                return hit;
            }
        }
        return null;
    }

    /// <summary>Vault folders from %APPDATA%\obsidian\obsidian.json, open vaults first.</summary>
    private static List<string> ObsidianVaults() {
        var result = new List<(string Path, bool Open, long Ts)>();
        try {
            string config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obsidian", "obsidian.json");
            if (!File.Exists(config)) {
                return [];
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(config));
            if (!doc.RootElement.TryGetProperty("vaults", out var vaults) || vaults.ValueKind != JsonValueKind.Object) {
                return [];
            }
            foreach (var vault in vaults.EnumerateObject()) {
                var v = vault.Value;
                if (!v.TryGetProperty("path", out var p) || p.GetString() is not { Length: > 0 } path
                    || !Directory.Exists(path)) {
                    continue;
                }
                bool open = v.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True;
                long ts = v.TryGetProperty("ts", out var t) && t.TryGetInt64(out long n) ? n : 0;
                result.Add((Path.GetFullPath(path), open, ts));
            }
        } catch (Exception ex) {
            Log.Write("image-refs: could not read Obsidian vaults: " + ex.Message);
        }
        return result
            .OrderByDescending(v => v.Open)
            .ThenByDescending(v => v.Ts)
            .Select(v => v.Path)
            .ToList();
    }

    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };
}
