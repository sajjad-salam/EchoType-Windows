using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace EchoType.Ui;

/// <summary>
/// IBM Plex Arabic, embedded in the exe. Registered twice: with GDI+ (so
/// <see cref="Font"/> objects can be built from it) and with GDI as a private
/// memory font (so <see cref="TextRenderer"/>, RichTextBox and the other
/// GDI-drawn controls resolve the same family by name).
/// </summary>
internal static class ArabicFont {

    private const string ResourceName = "EchoType.Fonts.IBMPlexArabic-Text.ttf";

    private static readonly object Gate = new();
    private static bool _loaded;
    private static PrivateFontCollection? _collection;
    private static FontFamily? _family;
    // AddMemoryFont needs the bytes to stay valid for the life of the process.
    private static IntPtr _memory;

    public static FontFamily? Family {
        get {
            EnsureLoaded();
            return _family;
        }
    }

    public static Font? Create(float size, FontStyle style = FontStyle.Regular) {
        var family = Family;
        if (family == null) {
            return null;
        }
        try {
            if (!family.IsStyleAvailable(style)) {
                style = FontStyle.Regular;
            }
            return new Font(family, size, style, GraphicsUnit.Point);
        } catch (ArgumentException) {
            return null;
        }
    }

    /// <summary>True when <paramref name="text"/> contains Arabic-script letters.</summary>
    public static bool HasArabic(string? text) {
        if (string.IsNullOrEmpty(text)) {
            return false;
        }
        foreach (char c in text) {
            if (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ'
                or >= 'ࢠ' and <= 'ࣿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿') {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the first strong letter is right-to-left (Arabic, Hebrew…), the same
    /// rule editors use to pick a paragraph direction.
    /// </summary>
    public static bool StartsRightToLeft(string? text) {
        if (string.IsNullOrEmpty(text)) {
            return false;
        }
        foreach (char c in text) {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ') {
                return true;
            }
            if (char.IsLetter(c)) {
                return false;
            }
        }
        return false;
    }

    private static void EnsureLoaded() {
        if (_loaded) {
            return;
        }
        lock (Gate) {
            if (_loaded) {
                return;
            }
            _loaded = true;
            try {
                using var stream = typeof(ArabicFont).Assembly.GetManifestResourceStream(ResourceName);
                if (stream == null) {
                    Log.Write("font: embedded Arabic font missing");
                    return;
                }
                byte[] bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                _memory = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, _memory, bytes.Length);

                uint installed = 0;
                if (AddFontMemResourceEx(_memory, (uint)bytes.Length, IntPtr.Zero, ref installed) == IntPtr.Zero) {
                    Log.Write("font: GDI registration of the Arabic font failed");
                }
                _collection = new PrivateFontCollection();
                _collection.AddMemoryFont(_memory, bytes.Length);
                _family = _collection.Families.FirstOrDefault();
                Log.Write("font: loaded " + (_family?.Name ?? "(none)"));
            } catch (Exception ex) {
                Log.Write("font: loading the Arabic font failed: " + ex.Message);
                _family = null;
            }
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
}
