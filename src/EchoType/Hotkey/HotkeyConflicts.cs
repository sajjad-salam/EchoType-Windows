using EchoType.Native;

namespace EchoType.Hotkey;

/// <summary>Shared collision checks for reserved hold and tap keys.</summary>
internal static class HotkeyConflicts {

    public static string? Message(int vk, Settings settings, params int[] ignoreVks) =>
        Message(HotkeyChord.Single(vk), settings, default, ignoreVks);

    public static string? Message(HotkeyChord chord, Settings settings, HotkeyChord ignoreChord) =>
        Message(chord, settings, ignoreChord, []);

    public static string? Message(
        HotkeyChord chord,
        Settings settings,
        HotkeyChord ignoreChord,
        params int[] ignoreVks) {
        if (chord.IsEmpty) {
            return null;
        }
        if (chord.ContainsEnter()) {
            return "Enter cannot be reserved — EchoType would swallow every Enter key.";
        }

        if (ConflictsWithReserved(chord, settings.HotkeyVk, ignoreVks)) {
            return chord.Count == 1
                ? $"That key is already the dictation shortcut ({HotkeyNames.For(settings.HotkeyVk)})."
                : $"That shortcut uses the dictation key ({HotkeyNames.For(settings.HotkeyVk)}).";
        }
        if (ConflictsWithReserved(chord, settings.PressEnterToggleVk, ignoreVks)) {
            return chord.Count == 1
                ? "That key is already the Auto Enter shortcut."
                : "That shortcut uses the Auto Enter key.";
        }
        if (ConflictsWithReserved(chord, settings.ChatGptSwitchVk, ignoreVks)) {
            return chord.Count == 1
                ? "That key already switches to ChatGPT."
                : "That shortcut uses the ChatGPT switch key.";
        }
        if (ConflictsWithReserved(chord, settings.GeminiSwitchVk, ignoreVks)) {
            return chord.Count == 1
                ? "That key already switches to Gemini."
                : "That shortcut uses the Gemini switch key.";
        }
        if (ConflictsWithReserved(chord, settings.ClaudeSwitchVk, ignoreVks)) {
            return chord.Count == 1
                ? "That key already switches to Claude."
                : "That shortcut uses the Claude switch key.";
        }
        if (ConflictsWithReserved(chord, settings.AskModelVk, ignoreVks)) {
            return chord.Count == 1
                ? "That key is already the Ask model shortcut."
                : "That shortcut uses the Ask model key.";
        }
        var translate = settings.TranslateChord;
        bool ignoreTranslate = (!ignoreChord.IsEmpty && translate.Equals(ignoreChord))
            || (translate.Count == 1 && ignoreVks.Contains((int)translate.K1));
        if (!ignoreTranslate && Conflicts(chord, translate)) {
            return chord.Count == 1 && translate.Count == 1
                ? "That key is already the Translate shortcut."
                : "That shortcut overlaps the Translate shortcut.";
        }
        if (ConflictsWithReserved(chord, settings.OpenModelWindowVk, ignoreVks)) {
            return chord.Count == 1
                ? "That key is already the model window shortcut."
                : "That shortcut uses the model window key.";
        }

        foreach (var cmd in settings.ActiveCommands) {
            var other = cmd.Chord;
            if (other.IsEmpty) {
                continue;
            }
            if (!ignoreChord.IsEmpty && other.Equals(ignoreChord)) {
                continue;
            }
            if (other.Count == 1 && ignoreVks.Contains((int)other.K1)) {
                continue;
            }
            if (Conflicts(chord, other)) {
                return other.Count == 1 && chord.Count == 1
                    ? "A custom command already uses that key."
                    : "A custom command already uses that shortcut.";
            }
        }
        return null;
    }

    private static bool ConflictsWithReserved(HotkeyChord chord, int reserved, int[] ignoreVks) {
        if (reserved == 0 || ignoreVks.Contains(reserved)) {
            return false;
        }
        var reservedChord = HotkeyChord.Single(reserved);
        return Conflicts(chord, reservedChord);
    }

    /// <summary>
    /// Same chord, or a fully reserved single key that appears in a multi-key
    /// chord (that key is swallowed globally, so the combo could never fire).
    /// Distinct multi-key chords may share modifiers.
    /// </summary>
    private static bool Conflicts(HotkeyChord a, HotkeyChord b) {
        if (a.IsEmpty || b.IsEmpty) {
            return false;
        }
        if (a.Equals(b)) {
            return true;
        }
        if (a.Count == 1 && b.Count > 1) {
            return b.ContainsExact(a.K1);
        }
        if (b.Count == 1 && a.Count > 1) {
            return a.ContainsExact(b.K1);
        }
        return false;
    }
}
