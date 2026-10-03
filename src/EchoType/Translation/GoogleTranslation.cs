using GTranslate.Translators;

namespace EchoType.Translation;

/// <summary>
/// Free Google Translate (no API key) through the GTranslate package. Used by the
/// Translate shortcut: the model page only transcribes, and the translation runs
/// here over plain HTTP — much faster than waiting for a ChatGPT/Gemini reply.
/// </summary>
internal static class GoogleTranslation {

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Both are thread-safe and hold their own HttpClient; keep them for the app's lifetime.
    private static readonly GoogleTranslator Primary = new();
    private static readonly GoogleTranslator2 Fallback = new();

    /// <summary>Target languages offered in the window and tray (code, display name).</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [
        ("en", "English"),
        ("ar", "Arabic"),
        ("fa", "Persian"),
        ("tr", "Turkish"),
        ("ku", "Kurdish (Kurmanji)"),
        ("ckb", "Kurdish (Sorani)"),
        ("ur", "Urdu"),
        ("hi", "Hindi"),
        ("bn", "Bengali"),
        ("fr", "French"),
        ("de", "German"),
        ("es", "Spanish"),
        ("pt", "Portuguese"),
        ("it", "Italian"),
        ("nl", "Dutch"),
        ("sv", "Swedish"),
        ("pl", "Polish"),
        ("uk", "Ukrainian"),
        ("ru", "Russian"),
        ("el", "Greek"),
        ("he", "Hebrew"),
        ("zh-CN", "Chinese (Simplified)"),
        ("zh-TW", "Chinese (Traditional)"),
        ("ja", "Japanese"),
        ("ko", "Korean"),
        ("id", "Indonesian"),
        ("ms", "Malay"),
        ("vi", "Vietnamese"),
        ("th", "Thai"),
    ];

    public static string NameFor(string code) {
        foreach (var (c, name) in Languages) {
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) {
                return name;
            }
        }
        return code;
    }

    /// <summary>
    /// Translates <paramref name="text"/> into <paramref name="toLanguage"/> (source
    /// language is auto-detected). Tries Google's main endpoint first, then the
    /// second one GTranslate knows. Throws when both fail or time out.
    /// </summary>
    public static async Task<string> TranslateAsync(string text, string toLanguage) {
        try {
            var result = await Primary.TranslateAsync(text, toLanguage).WaitAsync(Timeout);
            return result.Translation;
        } catch (Exception ex) {
            Log.Write("translate: primary endpoint failed (" + ex.Message + "), trying fallback");
        }
        var fallback = await Fallback.TranslateAsync(text, toLanguage).WaitAsync(Timeout);
        return fallback.Translation;
    }
}
