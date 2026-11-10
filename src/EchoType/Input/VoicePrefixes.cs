namespace EchoType.Input;

internal enum VoiceRoute {
    None,
    Ask,
    Translate,
}

/// <summary>
/// Optional spoken routing for plain dictation: starting with "ask …" / "اسأل …" sends the
/// rest to the Ask model, "translate …" / "ترجم …" translates it. Only the very first word
/// counts and something must follow it, so "Ask John to call me" still routes (that is the
/// feature) but "ask" on its own, or the word later in a sentence, is pasted as dictated.
/// </summary>
internal static class VoicePrefixes {

    private static readonly string[] AskWords = [
        "ask", "question",
        "اسأل", "اسال", "إسأل", "أسأل", "سؤال", "اسألي", "اساله", "اسأله",
    ];

    private static readonly string[] TranslateWords = [
        "translate", "translation",
        "ترجم", "ترجمة", "ترجمه", "ترجملي", "ترجمي",
    ];

    /// <summary>The route for <paramref name="transcript"/> and the text after the prefix word.</summary>
    public static (VoiceRoute Route, string Text) Match(string transcript) {
        string text = transcript.TrimStart();
        int end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && !IsPunctuation(text[end])) {
            end++;
        }
        if (end == 0) {
            return (VoiceRoute.None, transcript);
        }
        string first = Normalize(text[..end]);
        // Skip the prefix and any punctuation/spaces after it ("Ask, what…", "ترجم: …").
        int rest = end;
        while (rest < text.Length && (char.IsWhiteSpace(text[rest]) || IsPunctuation(text[rest]))) {
            rest++;
        }
        string remainder = text[rest..].Trim();
        if (remainder.Length == 0) {
            return (VoiceRoute.None, transcript);
        }
        if (AskWords.Any(w => Normalize(w) == first)) {
            return (VoiceRoute.Ask, Capitalize(remainder));
        }
        if (TranslateWords.Any(w => Normalize(w) == first)) {
            return (VoiceRoute.Translate, Capitalize(remainder));
        }
        return (VoiceRoute.None, transcript);
    }

    private static bool IsPunctuation(char c) => c is ',' or ':' or '.' or '،' or '؛' or ';' or '-' or '—' or '!' or '؟' or '?';

    /// <summary>Lowercase, with Arabic hamza/alef forms folded so spelling variants match.</summary>
    private static string Normalize(string word) {
        var chars = word.ToLowerInvariant().Select(c => c switch {
            'أ' or 'إ' or 'آ' => 'ا',
            'ة' => 'ه',
            'ى' => 'ي',
            _ => c,
        }).Where(c => c != 'ـ' && !(c >= 'ً' && c <= 'ْ')); // tatweel, diacritics
        return new string(chars.ToArray());
    }

    private static string Capitalize(string s) =>
        s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
}
