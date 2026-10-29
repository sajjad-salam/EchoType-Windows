using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using EchoType.Hotkey;
using EchoType.Native;

namespace EchoType;

/// <summary>
/// A spoken "switch my workspace" command. When a short dictation sounds like one of these
/// modes (e.g. "وضع البرمجة", "coding mode", "start coding"), EchoType presses
/// <see cref="Shortcut"/> instead of pasting, so a separate workspace app can react to it.
/// </summary>
internal sealed class WorkspaceMode {

    /// <summary>Dictations longer than this are always treated as normal text, never as a command.</summary>
    private const int MaxCommandWords = 8;

    /// <summary>Other words allowed next to a command word ("idea generation mode"); more means ordinary dictation.</summary>
    private const int MaxUnknownWords = 2;

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Shortcut to press, written like "Ctrl+Shift+F6".</summary>
    [JsonPropertyName("shortcut")]
    public string Shortcut { get; set; } = "";

    /// <summary>
    /// Words or short phrases that mean this mode, in any language (comma separated in the
    /// editor). The mode name is a keyword too.
    /// </summary>
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = [];

    public static List<WorkspaceMode> Defaults() => [
        new() {
            Name = "Coding mode",
            Shortcut = "Ctrl+Shift+F6",
            Keywords = ["وضع البرمجة", "برمجة", "برمج", "كود", "كودنك", "coding", "code", "programming", "program", "developer", "development"],
        },
        new() {
            Name = "Idea generation mode",
            Shortcut = "Ctrl+Shift+F7",
            Keywords = ["وضع انتاج الافكار", "افكار", "فكرة", "عصف ذهني", "idea", "ideas", "brainstorm", "brainstorming", "ideation"],
        },
        new() {
            Name = "Video editing mode",
            Shortcut = "Ctrl+Shift+F8",
            Keywords = ["وضع مونتاج الفيديوهات", "مونتاج", "منتجة", "تعديل الفيديو", "editing", "edit", "montage", "video editing"],
        },
        new() {
            Name = "Video publishing mode",
            Shortcut = "Ctrl+Shift+F9",
            Keywords = ["وضع نشر الفيديوهات", "نشر", "انشر", "رفع الفيديو", "publish", "publishing", "upload", "uploading", "posting"],
        },
    ];

    // ------------------------------------------------------------------
    // Matching
    // ------------------------------------------------------------------

    /// <summary>
    /// "Mode", "start", "switch", "وضع", "شغل"… — words that mark the dictation as a command.
    /// Stored normalized (see <see cref="Normalize"/>).
    /// </summary>
    private static readonly HashSet<string> ActivatorWords = new(new[] {
        "mode", "workflow", "workspace", "start", "begin", "switch", "change", "go", "open",
        "activate", "enable", "launch", "lets", "let",
        "وضع", "مود", "نمط", "ابدا", "ابدء", "بدء", "نبدا", "شغل", "تشغيل", "فعل", "تفعيل", "حول", "تحويل",
        "انتقل", "روح", "افتح", "خلي", "خليني", "خلينا", "يلا",
    }.Select(Normalize));

    /// <summary>Small words allowed around a command ("to", "the", "please", "على"…).</summary>
    private static readonly HashSet<string> FillerWords = new(new[] {
        "turn", "on", "run", "us", "me", "i", "want", "to", "into", "the", "a", "an", "my", "please", "now",
        "time", "for", "session", "work", "working", "video", "videos", "do", "some", "of", "and", "ok",
        "okay", "hey", "generation", "generating",
        "هسه", "هسا", "الان", "حاليا", "الي", "على", "علي", "لل", "ل", "من", "في", "و", "اريد", "ابي",
        "ابغي", "اني", "انا", "لو", "سمحت", "رجاء", "وقت", "جلسه", "عمل", "فيديو", "فيديوهات", "مال",
        "خل", "انتاج", "توليد",
    }.Select(Normalize));

    /// <summary>
    /// Returns the mode this dictation asks for, or null when it reads like ordinary text or is
    /// ambiguous between two modes. Only short utterances count: either just the mode's
    /// keywords ("coding", "وضع البرمجة") or a keyword with a command word ("start coding",
    /// "switch to idea mode"). Dictating a sentence that merely mentions "code" never matches.
    /// </summary>
    public static WorkspaceMode? Match(string transcript, IEnumerable<WorkspaceMode> modes) {
        string[] words = Tokenize(transcript);
        if (words.Length == 0 || words.Length > MaxCommandWords) {
            return null;
        }

        WorkspaceMode? best = null;
        int bestScore = 0;
        bool tie = false;
        bool[]? bestUsed = null;
        foreach (var mode in modes) {
            if (ParseShortcut(mode.Shortcut).Length == 0) {
                continue;
            }
            var used = new bool[words.Length];
            int score = mode.Score(words, used);
            if (score > bestScore) {
                best = mode;
                bestScore = score;
                bestUsed = used;
                tie = false;
            } else if (score > 0 && score == bestScore) {
                tie = true;
            }
        }
        if (best == null || tie || bestUsed == null) {
            return null;
        }

        int unknown = 0;
        bool activated = false;
        for (int i = 0; i < words.Length; i++) {
            if (bestUsed[i]) {
                continue;
            }
            string bare = StripArabicPrefix(words[i]);
            if (ActivatorWords.Contains(words[i]) || ActivatorWords.Contains(bare)) {
                activated = true;
            } else if (!FillerWords.Contains(words[i]) && !FillerWords.Contains(bare)) {
                unknown++;
            }
        }
        return unknown <= (activated ? MaxUnknownWords : 0) ? best : null;
    }

    /// <summary>Number of transcript words explained by this mode's name and keywords.</summary>
    private int Score(string[] words, bool[] used) {
        int score = 0;
        var phrases = Keywords.Append(Name)
            .Select(Tokenize)
            .Where(p => p.Length > 0)
            .OrderByDescending(p => p.Length);
        foreach (string[] phrase in phrases) {
            // A phrase counts when every one of its meaningful words is in the transcript.
            string[] core = phrase.Where(w => !FillerWords.Contains(w) && !ActivatorWords.Contains(w)).ToArray();
            if (core.Length == 0) {
                continue;
            }
            var hits = new List<int>();
            foreach (string kw in core) {
                int at = Array.FindIndex(words, w => WordMatches(w, kw));
                if (at < 0) {
                    hits.Clear();
                    break;
                }
                hits.Add(at);
            }
            foreach (int at in hits) {
                if (!used[at]) {
                    used[at] = true;
                    score++;
                }
            }
        }
        return score;
    }

    /// <summary>
    /// Forgiving word match: same word, same word without the Arabic "ال/و/ب/ل" prefix, one
    /// word containing the other (برمجة / البرمجه / ابرمج, program / programming), or a
    /// one-letter typo in longer words.
    /// </summary>
    private static bool WordMatches(string word, string keyword) {
        if (word == keyword) {
            return true;
        }
        string w = StripArabicPrefix(word);
        string k = StripArabicPrefix(keyword);
        if (w == k) {
            return true;
        }
        if (k.Length >= 4 && w.Contains(k, StringComparison.Ordinal)) {
            return true;
        }
        if (w.Length >= 4 && k.Contains(w, StringComparison.Ordinal)) {
            return true;
        }
        return w.Length >= 5 && k.Length >= 5 && Math.Abs(w.Length - k.Length) <= 1 && EditDistance(w, k) <= 1;
    }

    private static string StripArabicPrefix(string word) {
        foreach (string prefix in new[] { "وال", "بال", "لل", "ال" }) {
            if (word.Length - prefix.Length >= 3 && word.StartsWith(prefix, StringComparison.Ordinal)) {
                return word[prefix.Length..];
            }
        }
        return word;
    }

    private static int EditDistance(string a, string b) {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) {
            prev[j] = j;
        }
        for (int i = 1; i <= a.Length; i++) {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++) {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string[] Tokenize(string text) =>
        Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Lower-case, drop Arabic diacritics and tatweel, unify alef/ya/ta-marbuta spellings, and
    /// turn punctuation into spaces so "وضعُ البرمجةِ." and "وضع البرمجه" compare equal.
    /// </summary>
    internal static string Normalize(string text) {
        if (string.IsNullOrEmpty(text)) {
            return "";
        }
        var sb = new StringBuilder(text.Length);
        foreach (char raw in text.ToLowerInvariant()) {
            char c = raw switch {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ڤ' => 'ف',
                'گ' => 'ك',
                '\'' or '’' => '\0',
                _ => raw,
            };
            if (c == '\0' || c == 'ـ') {
                continue;
            }
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) {
                continue; // Arabic harakat
            }
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Shortcut
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses "Ctrl+Shift+F6", "ctrl + alt + k", "Win+1"… into virtual-key codes, modifiers
    /// first. Returns an empty array when the text has no main key, more than one main key,
    /// or names an unknown key.
    /// </summary>
    public static int[] ParseShortcut(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return [];
        }
        var modifiers = new List<int>();
        int key = 0;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            int vk = KeyFromName(part);
            if (vk <= 0) {
                return [];
            }
            if (IsModifier((uint)vk)) {
                if (!modifiers.Contains(vk)) {
                    modifiers.Add(vk);
                }
            } else if (key != 0) {
                return [];
            } else {
                key = vk;
            }
        }
        if (key == 0) {
            return [];
        }
        modifiers.Sort((a, b) => ModifierRank(a).CompareTo(ModifierRank(b)));
        return [.. modifiers, key];
    }

    /// <summary>Canonical display form, e.g. "Ctrl+Shift+F6"; empty when invalid.</summary>
    public static string FormatShortcut(string? text) =>
        string.Join("+", ParseShortcut(text).Select(vk => vk switch {
            (int)NativeMethods.VK_CONTROL => "Ctrl",
            (int)NativeMethods.VK_SHIFT => "Shift",
            (int)NativeMethods.VK_MENU => "Alt",
            0x5B => "Win",
            _ => HotkeyNames.For(vk),
        }));

    /// <summary>
    /// Presses and releases the shortcut in the foreground session (modifiers down, key tap,
    /// modifiers up). Tagged with <see cref="NativeMethods.EchoTypeExtraInfo"/> so EchoType's own
    /// hook ignores it; other apps' hotkeys (RegisterHotKey, hooks) still see it.
    /// </summary>
    public static bool SendShortcut(string? text) {
        int[] vks = ParseShortcut(text);
        if (vks.Length == 0) {
            return false;
        }
        var inputs = new NativeMethods.INPUT[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++) {
            inputs[i] = KeyInput((uint)vks[i], up: false);
            inputs[inputs.Length - 1 - i] = KeyInput((uint)vks[i], up: true);
        }
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length;
    }

    private static NativeMethods.INPUT KeyInput(uint vk, bool up) {
        // Navigation keys and the Win keys live on the extended part of the keyboard.
        bool extended = vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C;
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
        input.U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)vk,
            wScan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC),
            dwFlags = (up ? NativeMethods.KEYEVENTF_KEYUP : 0) | (extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0),
            dwExtraInfo = NativeMethods.EchoTypeExtraInfo,
        };
        return input;
    }

    private static int ModifierRank(int vk) => vk switch {
        (int)NativeMethods.VK_CONTROL => 0,
        (int)NativeMethods.VK_SHIFT => 1,
        (int)NativeMethods.VK_MENU => 2,
        _ => 3,
    };

    private static int KeyFromName(string name) {
        string n = name.Trim().ToLowerInvariant().Replace(" ", "");
        switch (n) {
            case "ctrl" or "control" or "ctl": return (int)NativeMethods.VK_CONTROL;
            case "shift": return (int)NativeMethods.VK_SHIFT;
            case "alt" or "menu": return (int)NativeMethods.VK_MENU;
            case "win" or "windows" or "meta": return 0x5B;
            case "esc" or "escape": return 0x1B;
            case "enter" or "return": return 0x0D;
            case "space": return 0x20;
            case "tab": return 0x09;
            case "pgup" or "pageup": return 0x21;
            case "pgdn" or "pagedown": return 0x22;
            case "del": return 0x2E;
            case "ins": return 0x2D;
        }
        if (n.Length == 1 && char.IsAsciiLetterOrDigit(n[0])) {
            return char.ToUpperInvariant(n[0]);
        }
        if (n.Length is 2 or 3 && n[0] == 'f' && int.TryParse(n[1..], out int f) && f is >= 1 and <= 24) {
            return 0x70 + f - 1; // VK_F1..VK_F24
        }
        return !n.All(char.IsAsciiDigit) && Enum.TryParse(n, ignoreCase: true, out Keys key) && key != Keys.None
            ? (int)(key & Keys.KeyCode)
            : 0;
    }

    private static bool IsModifier(uint vk) => HotkeyChord.Canonical(vk) is
        NativeMethods.VK_CONTROL or NativeMethods.VK_SHIFT or NativeMethods.VK_MENU or 0x5B;
}
