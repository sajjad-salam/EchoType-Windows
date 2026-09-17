namespace EchoType.Hotkey;

/// <summary>Human-readable names for virtual-key codes used as hold-to-talk keys.</summary>
internal static class HotkeyNames {

    public static string For(HotkeyChord chord) {
        if (chord.IsEmpty) {
            return "(none)";
        }
        if (chord.Count == 1) {
            return For((int)chord.K1);
        }
        string s = ComboName(chord.K1);
        for (int i = 1; i < chord.Count; i++) {
            s += " + " + ComboName(chord[i]);
        }
        return s;
    }

    public static string For(int vk) {
        if (vk <= 0) {
            return "(none)";
        }
        return vk switch {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "Caps Lock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left Arrow",
            0x26 => "Up Arrow",
            0x27 => "Right Arrow",
            0x28 => "Down Arrow",
            0x2D => "Insert",
            0x2E => "Delete",
            0x5B => "Left Win",
            0x5C => "Right Win",
            0x5D => "Menu",
            0x90 => "Num Lock",
            0x91 => "Scroll Lock",
            0xA0 => "Left Shift",
            0xA1 => "Right Shift",
            0xA2 => "Left Ctrl",
            0xA3 => "Right Ctrl",
            0xA4 => "Left Alt",
            0xA5 => "Right Alt",
            _ => Enum.IsDefined(typeof(Keys), vk)
                ? ((Keys)vk).ToString()
                : $"VK 0x{vk:X2}",
        };
    }

    private static string ComboName(uint vk) => HotkeyChord.Canonical(vk) switch {
        0x11 => "Ctrl",
        0x10 => "Shift",
        0x12 => "Alt",
        0x5B => "Win",
        _ => For((int)vk),
    };
}
