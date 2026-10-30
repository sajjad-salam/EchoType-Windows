using System.Text;
using System.Text.RegularExpressions;

namespace EchoType.Ui;

/// <summary>
/// Turns the Markdown a chat model answers in (headings, bold and italic, bulleted and
/// numbered lists, quotes, code, tables, rules) into RTF for a RichTextBox, so a reply reads
/// like it does on the model's own page instead of one flat run of text. Each paragraph
/// takes its own direction from its first strong letter, so Arabic lines run right to left
/// and English ones left to right in the same reply. <see cref="ToPlainText"/> gives the
/// same text without the markup, for pasting into fields that don't take formatting.
/// </summary>
internal static partial class MarkdownRtf {

    private enum Kind {
        Paragraph,
        Heading,
        Bullet,
        Number,
        Quote,
        Code,
        Rule,
        TableRow,
    }

    /// <param name="Level">Heading level (1–6), or the list / quote nesting depth (0 = top).</param>
    private sealed record Block(Kind Kind, string Text, int Level = 0, string Marker = "", bool Header = false);

    /// <summary>RTF for <paramref name="markdown"/> in <paramref name="fontName"/> at <paramref name="size"/> points.</summary>
    public static string ToRtf(string markdown, string fontName, float size) {
        var blocks = Parse(markdown);
        int body = (int)Math.Round(size * 2); // RTF sizes are in half-points
        var sb = new StringBuilder(markdown.Length * 3 + 512);
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\deflang1033");
        sb.Append(@"{\fonttbl{\f0\fnil\fcharset0 ").Append(Escape(fontName)).Append(@";}{\f1\fmodern\fcharset0 Consolas;}}");
        sb.Append(@"{\colortbl;");
        foreach (var c in new[] { HudTheme.Ink, HudTheme.InkMuted, HudTheme.Well, HudTheme.HairlineStrong, LinkColor }) {
            sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
        }
        sb.Append('}');
        sb.Append(@"\viewkind4\uc1");

        Kind previous = Kind.Paragraph;
        for (int i = 0; i < blocks.Count; i++) {
            var b = blocks[i];
            bool first = i == 0;
            bool rtl = ArabicFont.StartsRightToLeft(b.Kind == Kind.Rule ? FirstText(blocks, i) : b.Text);
            sb.Append(@"\pard\plain\f0\cf1\fs").Append(body);
            sb.Append(rtl ? @"\rtlpar\qr" : @"\ltrpar\ql");
            // A little air between blocks; list items and table rows stay close together.
            bool tight = b.Kind is Kind.Bullet or Kind.Number or Kind.TableRow or Kind.Code
                && previous == b.Kind && !first;
            int before = first ? 0 : b.Kind switch {
                Kind.Heading => b.Level <= 2 ? 280 : 200,
                _ when tight => 40,
                _ => 120,
            };
            sb.Append(@"\sb").Append(before).Append(@"\sa").Append(b.Kind == Kind.Heading ? 60 : 40);
            sb.Append(@"\sl300\slmult1 ");

            switch (b.Kind) {
                case Kind.Heading: {
                    float scale = b.Level switch { 1 => 1.5f, 2 => 1.3f, 3 => 1.15f, _ => 1.05f };
                    sb.Append(@"\b\fs").Append((int)Math.Round(body * scale)).Append(' ');
                    AppendInline(sb, b.Text, body);
                    sb.Append(@"\b0");
                    break;
                }
                case Kind.Bullet:
                case Kind.Number: {
                    int indent = 400 + b.Level * 400;
                    sb.Append(rtl ? @"\ri" : @"\li").Append(indent).Append(@"\fi-300 ");
                    sb.Append(@"\b ").Append(Unicode(b.Kind == Kind.Bullet ? (b.Level == 0 ? "•" : "◦") : b.Marker));
                    sb.Append(@"\b0\tab ");
                    AppendInline(sb, b.Text, body);
                    break;
                }
                case Kind.Quote:
                    sb.Append(rtl ? @"\ri" : @"\li").Append(360 + b.Level * 360).Append(@"\cf2\i ");
                    AppendInline(sb, b.Text, body);
                    sb.Append(@"\i0");
                    break;
                case Kind.Code:
                    // Code is always left to right, whatever the reply's language.
                    sb.Append(@"\ltrpar\ql\li240\ri240\f1\fs").Append(body - 2).Append(@"\highlight3 ");
                    AppendLines(sb, b.Text);
                    sb.Append(@"\highlight0");
                    break;
                case Kind.Rule:
                    sb.Append(@"\qc\cf4 ").Append(Unicode(new string('─', 24)));
                    break;
                case Kind.TableRow: {
                    if (b.Header) {
                        sb.Append(@"\b ");
                    }
                    var cells = b.Text.Split('\u001f');
                    for (int c = 0; c < cells.Length; c++) {
                        if (c > 0) {
                            sb.Append(@"\cf4 ").Append(Unicode("  │  ")).Append(@"\cf1 ");
                        }
                        AppendInline(sb, cells[c], body);
                    }
                    if (b.Header) {
                        sb.Append(@"\b0");
                    }
                    break;
                }
                default:
                    AppendInline(sb, b.Text, body);
                    break;
            }
            sb.Append(@"\par").Append('\n');
            previous = b.Kind;
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>The reply without Markdown markup, keeping its lines, bullets and numbers.</summary>
    public static string ToPlainText(string markdown) {
        var blocks = Parse(markdown);
        var sb = new StringBuilder(markdown.Length);
        Kind previous = Kind.Paragraph;
        for (int i = 0; i < blocks.Count; i++) {
            var b = blocks[i];
            if (i > 0) {
                bool tight = b.Kind is Kind.Bullet or Kind.Number or Kind.TableRow && previous == b.Kind;
                sb.Append(tight ? "\n" : "\n\n");
            }
            string indent = new(' ', b.Level * 2);
            switch (b.Kind) {
                case Kind.Bullet:
                    sb.Append(indent).Append(b.Level == 0 ? "• " : "◦ ").Append(StripInline(b.Text));
                    break;
                case Kind.Number:
                    sb.Append(indent).Append(b.Marker).Append(' ').Append(StripInline(b.Text));
                    break;
                case Kind.Code:
                    sb.Append(b.Text);
                    break;
                case Kind.Rule:
                    sb.Append("———");
                    break;
                case Kind.TableRow:
                    sb.Append(string.Join(" | ", b.Text.Split('\u001f').Select(StripInline)));
                    break;
                default:
                    sb.Append(StripInline(b.Text));
                    break;
            }
            previous = b.Kind;
        }
        return sb.ToString().Trim();
    }

    private static Color LinkColor => Color.FromArgb(0x1F, 0x5F, 0xD6);

    private static string FirstText(List<Block> blocks, int from) {
        for (int i = from; i < blocks.Count; i++) {
            if (blocks[i].Kind != Kind.Rule) {
                return blocks[i].Text;
            }
        }
        for (int i = from - 1; i >= 0; i--) {
            if (blocks[i].Kind != Kind.Rule) {
                return blocks[i].Text;
            }
        }
        return "";
    }

    // ------------------------------------------------------------------
    // Blocks
    // ------------------------------------------------------------------

    private static List<Block> Parse(string markdown) {
        var blocks = new List<Block>();
        string[] lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph() {
            if (paragraph.Count > 0) {
                blocks.Add(new Block(Kind.Paragraph, string.Join("\n", paragraph)));
                paragraph.Clear();
            }
        }

        for (int i = 0; i < lines.Length; i++) {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.Length == 0) {
                FlushParagraph();
                continue;
            }

            var fence = Fence().Match(trimmed);
            if (fence.Success) {
                FlushParagraph();
                string ticks = fence.Groups[1].Value;
                var code = new List<string>();
                for (i++; i < lines.Length; i++) {
                    if (lines[i].Trim().StartsWith(ticks, StringComparison.Ordinal)
                        && lines[i].Trim().Trim('`').Length == 0) {
                        break;
                    }
                    code.Add(lines[i]);
                }
                blocks.Add(new Block(Kind.Code, string.Join("\n", code)));
                continue;
            }

            var heading = Heading().Match(trimmed);
            if (heading.Success) {
                FlushParagraph();
                blocks.Add(new Block(Kind.Heading, heading.Groups[2].Value.Trim().TrimEnd('#').Trim(),
                    heading.Groups[1].Value.Length));
                continue;
            }

            if (Rule().IsMatch(trimmed)) {
                FlushParagraph();
                blocks.Add(new Block(Kind.Rule, ""));
                continue;
            }

            if (trimmed.StartsWith('|')) {
                FlushParagraph();
                bool header = i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim());
                blocks.Add(new Block(Kind.TableRow, string.Join('\u001f', Cells(trimmed)), Header: header));
                if (header) {
                    i++; // the |---|---| line
                }
                continue;
            }

            var quote = Quote().Match(line);
            if (quote.Success) {
                FlushParagraph();
                int depth = quote.Groups[1].Value.Count(c => c == '>') - 1;
                blocks.Add(new Block(Kind.Quote, quote.Groups[2].Value.Trim(), Math.Max(0, depth)));
                continue;
            }

            var bullet = BulletItem().Match(line);
            if (bullet.Success) {
                FlushParagraph();
                blocks.Add(new Block(Kind.Bullet, bullet.Groups[3].Value.Trim(), Depth(bullet.Groups[1].Value)));
                continue;
            }

            var number = NumberItem().Match(line);
            if (number.Success) {
                FlushParagraph();
                blocks.Add(new Block(Kind.Number, number.Groups[3].Value.Trim(), Depth(number.Groups[1].Value),
                    number.Groups[2].Value));
                continue;
            }

            // An indented line right after a list item continues that item.
            if (paragraph.Count == 0 && blocks.Count > 0 && line.StartsWith("  ", StringComparison.Ordinal)
                && blocks[^1].Kind is Kind.Bullet or Kind.Number) {
                var last = blocks[^1];
                blocks[^1] = last with { Text = last.Text + "\n" + trimmed };
                continue;
            }

            paragraph.Add(trimmed);
        }
        FlushParagraph();
        return blocks;
    }

    private static int Depth(string indent) {
        int spaces = indent.Replace("\t", "    ").Length;
        return Math.Min(4, spaces / 2);
    }

    private static IEnumerable<string> Cells(string row) {
        string inner = row.Trim();
        if (inner.StartsWith('|')) {
            inner = inner[1..];
        }
        if (inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal)) {
            inner = inner[..^1];
        }
        return CellSplit().Split(inner).Select(c => c.Replace("\\|", "|").Trim());
    }

    // ------------------------------------------------------------------
    // Inline: **bold**, *italic*, ~~strike~~, `code`, [links](url)
    // ------------------------------------------------------------------

    private static void AppendInline(StringBuilder sb, string text, int body) {
        int at = 0;
        foreach (Match m in Inline().Matches(text)) {
            AppendLines(sb, Unescape(text[at..m.Index]));
            if (m.Groups["code"].Success) {
                sb.Append(@"{\f1\fs").Append(body - 2).Append(@"\highlight3 ");
                sb.Append(Unicode(m.Groups["code"].Value));
                sb.Append('}');
            } else if (m.Groups["bold"].Success) {
                sb.Append(@"{\b ");
                AppendInline(sb, m.Groups["bold"].Value, body);
                sb.Append('}');
            } else if (m.Groups["italic"].Success) {
                sb.Append(@"{\i ");
                AppendInline(sb, m.Groups["italic"].Value, body);
                sb.Append('}');
            } else if (m.Groups["strike"].Success) {
                sb.Append(@"{\strike ");
                AppendInline(sb, m.Groups["strike"].Value, body);
                sb.Append('}');
            } else if (m.Groups["link"].Success) {
                sb.Append(@"{\cf5\ul ");
                AppendInline(sb, m.Groups["link"].Value, body);
                sb.Append('}');
            }
            at = m.Index + m.Length;
        }
        AppendLines(sb, Unescape(text[at..]));
    }

    private static string StripInline(string text) {
        string t = Inline().Replace(text, m =>
            m.Groups["code"].Success ? m.Groups["code"].Value
            : m.Groups["bold"].Success ? StripInline(m.Groups["bold"].Value)
            : m.Groups["italic"].Success ? StripInline(m.Groups["italic"].Value)
            : m.Groups["strike"].Success ? StripInline(m.Groups["strike"].Value)
            : m.Groups["link"].Success ? StripInline(m.Groups["link"].Value)
            : m.Value);
        return Unescape(t);
    }

    private static string Unescape(string s) => MarkdownEscape().Replace(s, "$1");

    /// <summary>Text with its line breaks kept as RTF line breaks inside the paragraph.</summary>
    private static void AppendLines(StringBuilder sb, string text) {
        string[] parts = text.Split('\n');
        for (int i = 0; i < parts.Length; i++) {
            if (i > 0) {
                sb.Append(@"\line ");
            }
            sb.Append(Unicode(parts[i]));
        }
    }

    /// <summary>RTF-escapes text: control characters, and everything past ASCII as \uN.</summary>
    private static string Unicode(string text) {
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text) {
            switch (c) {
                case '\\':
                case '{':
                case '}':
                    sb.Append('\\').Append(c);
                    break;
                case '\t':
                    sb.Append(@"\tab ");
                    break;
                default:
                    if (c < 0x20) {
                        break;
                    }
                    if (c < 0x80) {
                        sb.Append(c);
                    } else {
                        sb.Append(@"\u").Append((short)c).Append('?');
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Escape(string text) => Unicode(text).Replace(";", "");

    [GeneratedRegex(@"^(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex CellSplit();

    [GeneratedRegex(@"^\s*((?:>\s*)+)(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)([-*+•])\s+(.+)$")]
    private static partial Regex BulletItem();

    [GeneratedRegex(@"^(\s*)(\d{1,3}[.)])\s+(.+)$")]
    private static partial Regex NumberItem();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>.+?)\*\*|__(?<bold>.+?)__|~~(?<strike>.+?)~~|(?<![\w*])\*(?<italic>[^*\s](?:[^*]*[^*\s])?)\*(?![\w*])|!?\[(?<link>[^\]]+)\]\([^)\s]+\)")]
    private static partial Regex Inline();

    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+\-.!|>~])")]
    private static partial Regex MarkdownEscape();
}
