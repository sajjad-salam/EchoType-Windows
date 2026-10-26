using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EchoType;

/// <summary>
/// A manual "spoken word → written word" fix applied to every transcript, e.g. to keep a
/// dialect spelling (نقول → نكول) that the transcription model normalizes away.
/// </summary>
internal sealed class WordReplacement {

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>The rule for <paramref name="from"/> (same case-insensitive match as <see cref="Apply"/>), or null.</summary>
    public static WordReplacement? Find(IEnumerable<WordReplacement> rules, string from) {
        from = from.Trim();
        if (from.Length == 0) {
            return null;
        }
        return rules.FirstOrDefault(r =>
            string.Equals(r.From.Trim(), from, StringComparison.InvariantCultureIgnoreCase));
    }

    /// <summary>
    /// Selected text as a rule source: one short line without surrounding punctuation,
    /// or null when the selection is empty or looks like more than a word or phrase.
    /// </summary>
    public static string? WordFromSelection(string? selected) {
        string word = (selected ?? "").Trim()
            .Trim('.', ',', '،', '؛', ';', ':', '!', '?', '؟', '"', '\'', '(', ')', '«', '»')
            .Trim();
        return word.Length is > 0 and <= 60 && !word.Contains('\n') ? word : null;
    }

    /// <summary>Adds the rule, or updates the existing one for the same word. True when it was new.</summary>
    public static bool AddOrUpdate(List<WordReplacement> rules, string from, string to) {
        if (Find(rules, from) is { } existing) {
            existing.To = to;
            return false;
        }
        rules.Add(new WordReplacement { From = from.Trim(), To = to });
        return true;
    }

    /// <summary>
    /// Replaces whole words only (so "قال" does not touch "مقال"), case-insensitively.
    /// Longer source words run first so a phrase wins over a word inside it.
    /// </summary>
    public static string Apply(string text, IEnumerable<WordReplacement> rules) {
        if (string.IsNullOrEmpty(text)) {
            return text;
        }
        foreach (var rule in rules
                     .Where(r => !string.IsNullOrWhiteSpace(r.From))
                     .OrderByDescending(r => r.From.Trim().Length)) {
            string pattern = @"(?<![\w])" + Regex.Escape(rule.From.Trim()) + @"(?![\w])";
            string to = rule.To ?? "";
            text = Regex.Replace(text, pattern, _ => to, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }
}
