using System.Globalization;

namespace EchoType.Ui;

/// <summary>
/// UI language. English strings are the keys: <c>Loc.T("Listening")</c> returns the
/// Arabic text when Arabic is selected and the English text otherwise (or when a
/// string has no translation yet), so untranslated strings never show up blank.
/// Arabic also switches the window to right-to-left and the HUD font to IBM Plex Arabic.
/// </summary>
internal static partial class Loc {

    public const string English = "en";
    public const string ArabicCode = "ar";

    public static bool Arabic { get; private set; }

    /// <summary>Right-to-left layout (Arabic).</summary>
    public static bool Rtl => Arabic;

    /// <summary>
    /// Resolves the saved setting: "en", "ar", or empty/"auto" to follow the Windows
    /// display language.
    /// </summary>
    public static string Resolve(string? setting) {
        string code = (setting ?? "").Trim().ToLowerInvariant();
        if (code is ArabicCode or English) {
            return code;
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == ArabicCode ? ArabicCode : English;
    }

    /// <summary>Call once at startup, before any window or HUD font is created.</summary>
    public static void Apply(string? setting) {
        Arabic = Resolve(setting) == ArabicCode;
    }

    public static string T(string english) =>
        Arabic && Ar.TryGetValue(english, out string? arabic) ? arabic : english;

    /// <summary><see cref="string.Format(string, object?[])"/> over the translated format string.</summary>
    public static string F(string englishFormat, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(englishFormat), args);

    /// <summary>
    /// Mirrors horizontal alignment for right-to-left text: left becomes right and
    /// right becomes left; centered stays centered.
    /// </summary>
    public static TextFormatFlags Flags(TextFormatFlags flags) {
        if (!Rtl) {
            return flags;
        }
        flags |= TextFormatFlags.RightToLeft;
        if ((flags & TextFormatFlags.HorizontalCenter) != 0) {
            return flags;
        }
        return (flags & TextFormatFlags.Right) != 0
            ? flags & ~TextFormatFlags.Right
            : flags | TextFormatFlags.Right;
    }

    /// <summary>Flips <paramref name="r"/> inside a container <paramref name="width"/> wide when right-to-left.</summary>
    public static Rectangle Mirror(Rectangle r, int width) =>
        Rtl ? new Rectangle(width - r.Right, r.Y, r.Width, r.Height) : r;

    public static RectangleF Mirror(RectangleF r, float width) =>
        Rtl ? new RectangleF(width - r.Right, r.Y, r.Width, r.Height) : r;

    /// <summary>
    /// Right-to-left layout and the Arabic font for a standard WinForms dialog. Mirrors
    /// docking, anchors and FlowLayoutPanels automatically.
    /// </summary>
    public static void ApplyTo(Form form) {
        if (!Rtl) {
            return;
        }
        form.RightToLeft = RightToLeft.Yes;
        form.RightToLeftLayout = true;
        if (ArabicFont.Create(form.Font.SizeInPoints, form.Font.Style) is { } font) {
            form.Font = font;
        }
    }

    /// <summary>Right-to-left for a context menu and every submenu below it.</summary>
    public static void ApplyTo(ToolStrip strip) {
        if (!Rtl) {
            return;
        }
        strip.RightToLeft = RightToLeft.Yes;
        if (ArabicFont.Create(9.5f) is { } font) {
            strip.Font = font;
        }
    }
}
