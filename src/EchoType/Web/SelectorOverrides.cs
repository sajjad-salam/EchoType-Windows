using System.Text.Json;
using System.Text.RegularExpressions;

namespace EchoType.Web;

/// <summary>
/// Optional %APPDATA%\EchoType\selectors.json laid over the built-in <see cref="SelectorSet"/>s,
/// so a chat site's UI change can be patched straight from the log without a new build.
///
/// One object per site id ("chatgpt", "gemini", "claude"); keys are the names
/// <see cref="SelectorSet.Js"/> uses. Lists are put AHEAD of the built-in list (first
/// match wins, built-ins stay as fallbacks) unless the site sets "mode": "replace".
/// <code>
/// {
///   "chatgpt": {
///     "startCss": ["button[aria-label=\"Dictate\"]"],
///     "start": ["^dictate$"]
///   }
/// }
/// </code>
/// The file is re-read every time a chat page's WebView is created (launch, model
/// switch, self-heal reload), so a fix applies without restarting EchoType.
/// </summary>
internal static class SelectorOverrides {

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoType", "selectors.json");

    private static readonly (string Key, Func<SelectorSet, string[]> Get, Func<SelectorSet, string[], SelectorSet> Set, bool Regex)[] Lists = [
        ("composer", s => s.Composer, (s, v) => s with { Composer = v }, false),
        ("start", s => s.Start, (s, v) => s with { Start = v }, true),
        ("startCss", s => s.StartCss, (s, v) => s with { StartCss = v }, false),
        ("submit", s => s.Submit, (s, v) => s with { Submit = v }, true),
        ("submitCss", s => s.SubmitCss, (s, v) => s with { SubmitCss = v }, false),
        ("cancel", s => s.Cancel, (s, v) => s with { Cancel = v }, true),
        ("cancelCss", s => s.CancelCss, (s, v) => s with { CancelCss = v }, false),
        ("neverClick", s => s.NeverClick, (s, v) => s with { NeverClick = v }, true),
        ("send", s => s.Send, (s, v) => s with { Send = v }, true),
        ("sendCss", s => s.SendCss, (s, v) => s with { SendCss = v }, false),
        ("stop", s => s.Stop, (s, v) => s with { Stop = v }, true),
        ("stopCss", s => s.StopCss, (s, v) => s with { StopCss = v }, false),
        ("newChat", s => s.NewChat, (s, v) => s with { NewChat = v }, true),
        ("newChatCss", s => s.NewChatCss, (s, v) => s with { NewChatCss = v }, false),
        ("openSidebar", s => s.OpenSidebar, (s, v) => s with { OpenSidebar = v }, true),
        ("openSidebarCss", s => s.OpenSidebarCss, (s, v) => s with { OpenSidebarCss = v }, false),
        ("dictatingCss", s => s.DictatingCss, (s, v) => s with { DictatingCss = v }, false),
        ("generatingCss", s => s.GeneratingCss, (s, v) => s with { GeneratingCss = v }, false),
        ("assistant", s => s.Assistant, (s, v) => s with { Assistant = v }, false),
        ("assistantMarkdown", s => s.AssistantMarkdown, (s, v) => s with { AssistantMarkdown = v }, false),
        ("user", s => s.User, (s, v) => s with { User = v }, false),
    ];

    private static readonly (string Key, Func<SelectorSet, bool, SelectorSet> Set)[] Flags = [
        ("hasDictationShortcut", (s, v) => s with { HasDictationShortcut = v }),
        ("hasDictationSubmitShortcut", (s, v) => s with { HasDictationSubmitShortcut = v }),
        ("submitFallsBackToStart", (s, v) => s with { SubmitFallsBackToStart = v }),
        ("treatGumAsEngaged", (s, v) => s with { TreatGumAsEngaged = v }),
        ("useUserMessageAsTranscriptFallback", (s, v) => s with { UseUserMessageAsTranscriptFallback = v }),
        ("useAccessibilityTranscriptFallback", (s, v) => s with { UseAccessibilityTranscriptFallback = v }),
        ("liveTranscript", (s, v) => s with { LiveTranscript = v }),
    ];

    /// <summary>
    /// Built-in selectors for <paramref name="siteId"/> with any selectors.json overrides
    /// applied. A missing or broken file logs and falls back to the built-ins.
    /// </summary>
    public static SelectorSet Apply(string siteId, SelectorSet builtIn) {
        if (!File.Exists(FilePath)) {
            return builtIn;
        }
        try {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), new JsonDocumentOptions {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                Log.Write("selectors: " + FilePath + " must be a JSON object — ignored");
                return builtIn;
            }
            JsonElement site = default;
            bool found = false;
            foreach (var prop in doc.RootElement.EnumerateObject()) {
                if (prop.Name.Equals(siteId, StringComparison.OrdinalIgnoreCase)) {
                    site = prop.Value;
                    found = true;
                    break;
                }
            }
            if (!found) {
                return builtIn;
            }
            if (site.ValueKind != JsonValueKind.Object) {
                Log.Write($"selectors: \"{siteId}\" in selectors.json must be an object — ignored");
                return builtIn;
            }
            return ApplySite(siteId, site, builtIn);
        } catch (Exception ex) {
            Log.Write("selectors: couldn't read " + FilePath + " (" + ex.Message + ") — using built-in selectors");
            return builtIn;
        }
    }

    private static SelectorSet ApplySite(string siteId, JsonElement site, SelectorSet set) {
        bool replace = site.TryGetProperty("mode", out var mode)
            && mode.ValueKind == JsonValueKind.String
            && string.Equals(mode.GetString(), "replace", StringComparison.OrdinalIgnoreCase);
        var applied = new List<string>();

        foreach (var prop in site.EnumerateObject()) {
            string key = prop.Name;
            if (key.Equals("mode", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var list = Lists.FirstOrDefault(l => l.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (list.Key != null) {
                string[]? values = ReadList(siteId, key, prop.Value, list.Regex);
                if (values == null) {
                    continue;
                }
                string[] merged = replace ? values : values.Concat(list.Get(set)).Distinct(StringComparer.Ordinal).ToArray();
                set = list.Set(set, merged);
                applied.Add(key + "(" + values.Length + ")");
                continue;
            }
            var flag = Flags.FirstOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (flag.Key != null) {
                if (prop.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) {
                    Log.Write($"selectors: {siteId}.{key} must be true or false — ignored");
                    continue;
                }
                set = flag.Set(set, prop.Value.GetBoolean());
                applied.Add(key + "=" + (prop.Value.GetBoolean() ? "true" : "false"));
                continue;
            }
            if (key.Equals("loggedOutMarker", StringComparison.OrdinalIgnoreCase)) {
                if (prop.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(prop.Value.GetString())) {
                    Log.Write($"selectors: {siteId}.{key} must be a CSS selector string — ignored");
                    continue;
                }
                set = set with { LoggedOutMarker = prop.Value.GetString()! };
                applied.Add(key);
                continue;
            }
            if (key.Equals("transcriptSettlePolls", StringComparison.OrdinalIgnoreCase)) {
                if (!prop.Value.TryGetInt32(out int polls) || polls < 1 || polls > 40) {
                    Log.Write($"selectors: {siteId}.{key} must be a number from 1 to 40 — ignored");
                    continue;
                }
                set = set with { TranscriptSettlePolls = polls };
                applied.Add(key + "=" + polls);
                continue;
            }
            Log.Write($"selectors: unknown key {siteId}.{key} in selectors.json — ignored");
        }

        if (applied.Count > 0) {
            Log.Write($"selectors: {siteId} overrides from selectors.json ({(replace ? "replace" : "prepend")}): "
                + string.Join(", ", applied));
        }
        return set;
    }

    private static string[]? ReadList(string siteId, string key, JsonElement value, bool regex) {
        if (value.ValueKind != JsonValueKind.Array) {
            Log.Write($"selectors: {siteId}.{key} must be a list of strings — ignored");
            return null;
        }
        var values = new List<string>();
        foreach (var item in value.EnumerateArray()) {
            string? s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(s)) {
                Log.Write($"selectors: {siteId}.{key} has a non-string or empty entry — skipped");
                continue;
            }
            // Button patterns run as JavaScript regexes; .NET's syntax is close enough
            // to catch typos here instead of silently never matching in the page.
            if (regex && !IsValidRegex(s)) {
                Log.Write($"selectors: {siteId}.{key} pattern \"{s}\" is not a valid regex — skipped");
                continue;
            }
            values.Add(s);
        }
        return values.ToArray();
    }

    private static bool IsValidRegex(string pattern) {
        try {
            _ = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            return true;
        } catch (ArgumentException) {
            return false;
        }
    }
}
