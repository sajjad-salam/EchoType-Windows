using System.Text.RegularExpressions;
using GTranslate.Translators;

namespace EchoType.Translation;

/// <summary>
/// Free Google Translate (no API key) through the GTranslate package. Used by the
/// Translate shortcut when no OpenAI API key is set (or the OpenAI call fails): the
/// model page only transcribes, and the translation runs here over plain HTTP —
/// much faster than waiting for a ChatGPT/Gemini reply.
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
    /// <remarks>
    /// Google detects one source language for the whole text. With mixed text (Arabic
    /// with English terms) it often detects the target language itself and returns those
    /// terms untouched. A second pass translates the leftover runs written in another
    /// script (Latin words in an Arabic translation, Arabic words in an English one…).
    /// </remarks>
    public static async Task<string> TranslateAsync(string text, string toLanguage) {
        string translated = await TranslateOnceAsync(text, toLanguage);
        try {
            return await TranslateLeftoversAsync(translated, toLanguage);
        } catch (Exception ex) {
            Log.Write("translate: mixed-language pass failed (" + ex.Message + "), keeping first pass");
            return translated;
        }
    }

    private static async Task<string> TranslateOnceAsync(string text, string toLanguage, string? fromLanguage = null) {
        try {
            var result = await Primary.TranslateAsync(text, toLanguage, fromLanguage).WaitAsync(Timeout);
            return result.Translation;
        } catch (Exception ex) {
            Log.Write("translate: primary endpoint failed (" + ex.Message + "), trying fallback");
        }
        var fallback = await Fallback.TranslateAsync(text, toLanguage, fromLanguage).WaitAsync(Timeout);
        return fallback.Translation;
    }

    // Script families the second pass knows how to spot.
    private const string LatinLetters = @"A-Za-z\u00C0-\u024F";
    private const string ArabicLetters = @"\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF\uFB50-\uFDFF\uFE70-\uFEFF";

    // A run of words in one script, joined by spaces/hyphens/apostrophes (keeps "machine learning" together).
    private static readonly Regex LatinRun = new(
        "[" + LatinLetters + "]+(?:['’\\-][" + LatinLetters + "]+)*(?:\\s+[" + LatinLetters + "]+(?:['’\\-][" + LatinLetters + "]+)*)*",
        RegexOptions.Compiled);
    private static readonly Regex ArabicRun = new(
        "[" + ArabicLetters + "]+(?:\\s+[" + ArabicLetters + "]+)*",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ArabicScriptTargets = new(StringComparer.OrdinalIgnoreCase) {
        "ar", "fa", "ur", "ckb",
    };

    private static readonly HashSet<string> LatinScriptTargets = new(StringComparer.OrdinalIgnoreCase) {
        "en", "fr", "de", "es", "pt", "it", "nl", "sv", "pl", "tr", "ku", "id", "ms", "vi",
    };

    private const int MaxLeftoverRuns = 12;

    private static async Task<string> TranslateLeftoversAsync(string translated, string toLanguage) {
        var foreign = new List<Regex>(2);
        if (!LatinScriptTargets.Contains(toLanguage)) {
            foreign.Add(LatinRun);
        }
        if (!ArabicScriptTargets.Contains(toLanguage)) {
            foreign.Add(ArabicRun);
        }

        var runs = new List<string>();
        foreach (var regex in foreign) {
            foreach (Match m in regex.Matches(translated)) {
                string run = m.Value;
                if (!LooksTranslatable(run) || runs.Contains(run, StringComparer.Ordinal)) {
                    continue;
                }
                runs.Add(run);
            }
        }
        if (runs.Count == 0) {
            return translated;
        }
        if (runs.Count > MaxLeftoverRuns) {
            runs = runs.OrderByDescending(r => r.Length).Take(MaxLeftoverRuns).ToList();
        }
        Log.Write("translate: " + runs.Count + " untranslated run(s) left, translating them separately");

        // In parallel so the extra pass costs about one more round trip.
        string[] results = await Task.WhenAll(runs.Select(async run => {
            try {
                return (await TranslateOnceAsync(run, toLanguage)).Trim();
            } catch (Exception ex) {
                Log.Write("translate: run failed (" + ex.Message + ")");
                return run;
            }
        }));

        // Longest first so "machine learning" is replaced before a lone "learning".
        var pairs = runs.Zip(results)
            .Where(p => p.Second.Length > 0 && p.Second != p.First)
            .OrderByDescending(p => p.First.Length);
        foreach (var (source, replacement) in pairs) {
            translated = Regex.Replace(translated,
                @"(?<![\p{L}\p{Nd}])" + Regex.Escape(source) + @"(?![\p{L}\p{Nd}])",
                replacement.Replace("$", "$$"));
        }
        return translated;
    }

    // Leave alone what Google would not translate anyway: single letters, acronyms
    // (API, PDF), and identifiers with inner capitals (ChatGPT, iPhone, WhatsApp).
    private static bool LooksTranslatable(string run) {
        if (run.Length < 2) {
            return false;
        }
        if (run.Contains(' ')) {
            return true;
        }
        bool allUpper = run.All(c => !char.IsLetter(c) || char.IsUpper(c));
        if (allUpper && run.Length <= 6) {
            return false;
        }
        for (int i = 1; i < run.Length; i++) {
            if (char.IsUpper(run[i]) && char.IsLower(run[i - 1])) {
                return false;
            }
        }
        return true;
    }
}
