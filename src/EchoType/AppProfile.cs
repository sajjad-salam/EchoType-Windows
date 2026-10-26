using System.Text.Json.Serialization;

namespace EchoType;

/// <summary>
/// Per-app overrides keyed on the process name of the window dictation started in
/// (e.g. Telegram → Enter on with a dialect cleaning prompt, Code → Enter off, no clean).
/// A null or empty field keeps the global setting.
/// </summary>
internal sealed class AppProfile {

    /// <summary>Process names without ".exe", e.g. "Telegram", "Code". Case-insensitive.</summary>
    [JsonPropertyName("apps")]
    public List<string> Apps { get; set; } = [];

    /// <summary>Overrides <see cref="Settings.PressEnterAfterPaste"/>. Null = global.</summary>
    [JsonPropertyName("pressEnter")]
    public bool? PressEnter { get; set; }

    /// <summary>Overrides <see cref="Settings.AutoClean"/>. Null = global.</summary>
    [JsonPropertyName("autoClean")]
    public bool? AutoClean { get; set; }

    /// <summary>Overrides <see cref="Settings.AutoCleanPrompt"/>. Empty = global.</summary>
    [JsonPropertyName("cleanPrompt")]
    public string CleanPrompt { get; set; } = "";

    /// <summary>Overrides <see cref="Settings.TranslateTargetLanguage"/> for the Translate shortcut. Empty = global.</summary>
    [JsonPropertyName("translateTo")]
    public string TranslateTo { get; set; } = "";

    [JsonIgnore]
    public string DisplayName => string.Join(", ", Apps);

    public void Normalize() {
        Apps = (Apps ?? [])
            .Select(NormalizeApp)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        CleanPrompt = (CleanPrompt ?? "").Trim();
        TranslateTo = (TranslateTo ?? "").Trim();
    }

    public static string NormalizeApp(string app) {
        string name = (app ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].Trim() : name;
    }

    /// <summary>
    /// True when <paramref name="process"/> is one of <see cref="Apps"/>, or a dotted variant of
    /// one ("WhatsApp" also matches "WhatsApp.Root").
    /// </summary>
    public bool Matches(string process) =>
        Apps.Any(a => string.Equals(process, a, StringComparison.OrdinalIgnoreCase)
            || process.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>The first profile listing <paramref name="process"/>, or null.</summary>
    public static AppProfile? Match(string? process, IEnumerable<AppProfile> profiles) {
        if (string.IsNullOrWhiteSpace(process)) {
            return null;
        }
        return profiles.FirstOrDefault(p => p.Matches(process));
    }
}
