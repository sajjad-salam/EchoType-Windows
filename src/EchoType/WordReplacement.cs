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
