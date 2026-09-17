using EchoType.Native;

namespace EchoType.Hotkey;

/// <summary>
/// A reserved shortcut of one, two, or three keys (for example F8, Ctrl+Space,
/// or Ctrl+Shift+Space). Multi-key chords treat left/right modifiers as the
/// same key; a lone Left/Right Ctrl stays side-specific, matching dictation.
/// </summary>
internal readonly struct HotkeyChord : IEquatable<HotkeyChord> {

    public const int MaxKeys = 3;

    public uint K1 { get; }
    public uint K2 { get; }
    public uint K3 { get; }
    public int Count { get; }

    public bool IsEmpty => Count <= 0;

    private HotkeyChord(uint k1, uint k2, uint k3, int count) {
        K1 = k1;
        K2 = k2;
        K3 = k3;
        Count = count;
    }

    public uint this[int index] => index switch {
        0 => K1,
        1 => K2,
        _ => K3,
    };

    public static HotkeyChord Single(int vk) =>
        vk <= 0 ? default : new((uint)vk, 0, 0, 1);

    public static HotkeyChord FromVks(IReadOnlyList<int>? vks, int fallbackVk = 0) {
        uint a = 0, b = 0, c = 0;
        int n = 0;
        if (vks != null) {
            foreach (int vk in vks) {
                if (vk <= 0 || n >= MaxKeys) {
                    continue;
                }
                if (n == 0) {
                    a = (uint)vk;
                } else if (n == 1) {
                    b = (uint)vk;
                } else {
                    c = (uint)vk;
                }
                n++;
            }
        }
        if (n == 0 && fallbackVk > 0) {
            a = (uint)fallbackVk;
            n = 1;
        }
        return Build(a, b, c, n);
    }

    /// <summary>Build from hook capture buffers. Must not allocate.</summary>
    public static HotkeyChord FromCaptured(uint[] keys, int count) {
        if (count <= 0) {
            return default;
        }
        uint a = keys[0];
        uint b = count > 1 ? keys[1] : 0;
        uint c = count > 2 ? keys[2] : 0;
        return Build(a, b, c, count < MaxKeys ? count : MaxKeys);
    }

    public int[] ToIntArray() {
        if (Count <= 0) {
            return [];
        }
        if (Count == 1) {
            return [(int)K1];
        }
        if (Count == 2) {
            return [(int)K1, (int)K2];
        }
        return [(int)K1, (int)K2, (int)K3];
    }

    /// <summary>Trigger key used as <c>hotkeyVk</c> for older config files.</summary>
    public int LegacyVk => Count <= 0 ? 0 : (int)this[Count - 1];

    public bool ContainsEnter() {
        for (int i = 0; i < Count; i++) {
            if (this[i] == NativeMethods.VK_RETURN) {
                return true;
            }
        }
        return false;
    }

    public bool ContainsExact(uint vk) {
        for (int i = 0; i < Count; i++) {
            if (this[i] == vk) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when this chord should fire given the keys currently down.
    /// One-key chords match the event only (other keys may be down). Multi-key
    /// chords require an exact set match, with left/right modifiers equivalent.
    /// </summary>
    public bool MatchesPressed(uint[] pressed, int pressedCount, uint distinguished, uint rawVk) {
        if (Count <= 0) {
            return false;
        }
        if (Count == 1) {
            return K1 == distinguished || K1 == rawVk;
        }
        if (pressedCount != Count) {
            return false;
        }
        for (int i = 0; i < Count; i++) {
            if (!IsAmong(this[i], pressed, pressedCount)) {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether a physical key belongs to this chord (for hold-end).</summary>
    public bool ContainsPhysical(uint distinguished, uint rawVk) {
        if (Count <= 0) {
            return false;
        }
        if (Count == 1) {
            return K1 == distinguished || K1 == rawVk;
        }
        for (int i = 0; i < Count; i++) {
            if (SameKey(this[i], distinguished) || SameKey(this[i], rawVk)) {
                return true;
            }
        }
        return false;
    }

    public bool Equals(HotkeyChord other) {
        if (Count != other.Count) {
            return false;
        }
        if (Count == 0) {
            return true;
        }
        if (Count == 1) {
            return K1 == other.K1;
        }
        for (int i = 0; i < Count; i++) {
            uint key = this[i];
            bool found = false;
            for (int j = 0; j < other.Count; j++) {
                if (SameKey(key, other[j])) {
                    found = true;
                    break;
                }
            }
            if (!found) {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is HotkeyChord other && Equals(other);

    public override int GetHashCode() {
        if (Count <= 0) {
            return 0;
        }
        if (Count == 1) {
            return (int)K1;
        }
        uint a = Canonical(K1);
        uint b = Count > 1 ? Canonical(K2) : 0;
        uint c = Count > 2 ? Canonical(K3) : 0;
        return HashCode.Combine(Count, a, b, c);
    }

    public static bool operator ==(HotkeyChord left, HotkeyChord right) => left.Equals(right);

    public static bool operator !=(HotkeyChord left, HotkeyChord right) => !left.Equals(right);

    public static bool SameKey(uint a, uint b) {
        if (a == b) {
            return true;
        }
        uint ca = Canonical(a);
        uint cb = Canonical(b);
        return ca == cb && IsModifierFamily(ca);
    }

    public static uint Canonical(uint vk) => vk switch {
        NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            => NativeMethods.VK_CONTROL,
        NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            => NativeMethods.VK_SHIFT,
        NativeMethods.VK_MENU or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
            => NativeMethods.VK_MENU,
        0x5B or 0x5C => 0x5B,
        _ => vk,
    };

    private static bool IsModifierFamily(uint canonical) =>
        canonical is NativeMethods.VK_CONTROL or NativeMethods.VK_SHIFT or NativeMethods.VK_MENU or 0x5B;

    private static bool IsAmong(uint key, uint[] pressed, int pressedCount) {
        for (int i = 0; i < pressedCount; i++) {
            if (SameKey(key, pressed[i])) {
                return true;
            }
        }
        return false;
    }

    private static HotkeyChord Build(uint a, uint b, uint c, int n) {
        if (n <= 0) {
            return default;
        }
        if (n == 1) {
            return new(a, 0, 0, 1);
        }

        uint x = Canonical(a);
        uint y = Canonical(b);
        uint z = n > 2 ? Canonical(c) : 0;

        if (SameKey(x, y)) {
            y = z;
            z = 0;
            n--;
        }
        if (n > 2 && SameKey(x, z)) {
            z = 0;
            n--;
        }
        if (n > 2 && SameKey(y, z)) {
            z = 0;
            n--;
        }
        if (n <= 1) {
            return new(a, 0, 0, 1);
        }

        SortByRank(ref x, ref y, ref z, n);
        return new(x, y, n > 2 ? z : 0, n);
    }

    private static void SortByRank(ref uint x, ref uint y, ref uint z, int n) {
        if (Rank(y) < Rank(x)) {
            (x, y) = (y, x);
        }
        if (n >= 3) {
            if (Rank(z) < Rank(y)) {
                (y, z) = (z, y);
            }
            if (Rank(y) < Rank(x)) {
                (x, y) = (y, x);
            }
        }
    }

    private static int Rank(uint vk) => Canonical(vk) switch {
        NativeMethods.VK_CONTROL => 0,
        NativeMethods.VK_SHIFT => 1,
        NativeMethods.VK_MENU => 2,
        0x5B => 3,
        _ => 10,
    };
}
