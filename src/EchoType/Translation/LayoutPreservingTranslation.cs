using System.Text;
using System.Text.RegularExpressions;

namespace EchoType.Translation;

/// <summary>
/// Translates a block of text without losing its shape. Translators usually hand back one
/// paragraph: line breaks merge, blank lines, indentation and runs of spaces (aligned
/// columns) disappear, list markers move. Here the text is cut into a layout template —
/// line breaks, blank lines, indentation, bullets and numbers, tabs and runs of two or more
/// spaces, trailing spaces — and the pieces of words in between. Only the pieces are
/// translated (all in one request, so they keep each other's context), then put back into
/// the template, so every line and gap stays exactly where it was.
/// </summary>
internal static partial class LayoutPreservingTranslation {

    /// <summary>Translates <c>pieces</c> and returns exactly one translation per piece, in order.</summary>
    public delegate Task<IReadOnlyList<string>> PieceTranslator(IReadOnlyList<string> pieces);

    private readonly record struct Part(string Text, bool Translate);

    public static async Task<string> TranslateAsync(string text, PieceTranslator translate) {
        List<Part> parts = Split(text);
        var pieces = parts.Where(p => p.Translate).Select(p => p.Text).ToList();
        if (pieces.Count == 0) {
            return text;
        }
        IReadOnlyList<string> translated = await translate(pieces);
        if (translated.Count != pieces.Count) {
            throw new InvalidOperationException("Got " + translated.Count + " translations for " + pieces.Count + " pieces.");
        }

        var sb = new StringBuilder(text.Length * 3 / 2);
        int i = 0;
        foreach (var part in parts) {
            if (!part.Translate) {
                sb.Append(part.Text);
                continue;
            }
            // A piece sits inside one line: a line break in its translation would shift the layout.
            string t = LineBreaks().Replace(translated[i], " ").Trim();
            sb.Append(t.Length > 0 ? t : part.Text);
            i++;
        }
        return sb.ToString();
    }

    private static List<Part> Split(string text) {
        var parts = new List<Part>();
        // The capture group keeps each line break ("\r\n", "\n" or "\r") as its own entry.
        foreach (string chunk in LineSplit().Split(text)) {
            if (chunk.Length == 0) {
                continue;
            }
            if (chunk[0] is '\r' or '\n') {
                parts.Add(new Part(chunk, false));
                continue;
            }
            SplitLine(chunk, parts);
        }
        return parts;
    }

    private static void SplitLine(string line, List<Part> parts) {
        int start = LinePrefix().Match(line).Length;
        int end = line.Length;
        while (end > start && line[end - 1] is ' ' or '\t' or ' ' or '　') {
            end--;
        }
        if (start > 0) {
            parts.Add(new Part(line[..start], false));
        }
        int at = start;
        foreach (Match gap in Gap().Matches(line[start..end])) {
            AddText(line.Substring(at, start + gap.Index - at), parts);
            parts.Add(new Part(gap.Value, false));
            at = start + gap.Index + gap.Length;
        }
        AddText(line[at..end], parts);
        if (end < line.Length) {
            parts.Add(new Part(line[end..], false));
        }
    }

    // Only text with letters is sent; numbers, symbols and separators ("---", "12:30") stay as they are.
    private static void AddText(string text, List<Part> parts) {
        if (text.Length > 0) {
            parts.Add(new Part(text, text.Any(char.IsLetter)));
        }
    }

    [GeneratedRegex(@"(\r\n|\n|\r)")]
    private static partial Regex LineSplit();

    [GeneratedRegex(@"\r\n|\n|\r")]
    private static partial Regex LineBreaks();

    // Indentation plus any list markers: "  - ", "• ", "1. ", "2) ", "(3) ", "a) ", "> ", "[x] ".
    [GeneratedRegex(@"^[ \t 　]*(?:(?:[-*+•◦▪‣·–—>]|\p{Nd}{1,3}[.)]|\(\p{Nd}{1,3}\)|[A-Za-z][.)]|\[[ xX]\])[ \t]+)*")]
    private static partial Regex LinePrefix();

    // Spacing that carries layout inside a line: tabs, or two or more spaces.
    [GeneratedRegex(@"[  ]*\t[ \t ]*|[  　]{2,}")]
    private static partial Regex Gap();
}
