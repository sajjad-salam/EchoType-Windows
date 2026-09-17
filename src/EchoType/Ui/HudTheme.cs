using System.Drawing.Drawing2D;
using System.Drawing.Text;
using EchoType.Native;

namespace EchoType.Ui;

/// <summary>
/// Visual identity shared by the status HUD and the main window: the dark
/// rounded pill, hairline border, soft shadow, and the HUD type scale.
/// </summary>
internal static class HudTheme {

    public static readonly Color Window = Color.FromArgb(255, 10, 11, 13);
    public static readonly Color Pill = Color.FromArgb(242, 18, 20, 24);
    public static readonly Color Card = Color.FromArgb(255, 18, 20, 24);
    public static readonly Color Border = Color.FromArgb(48, 255, 255, 255);
    public static readonly Color Title = Color.FromArgb(210, 214, 220);
    public static readonly Color Body = Color.FromArgb(236, 240, 244);
    public static readonly Color Muted = Color.FromArgb(148, 156, 168);
    public static readonly Color Teal = Color.FromArgb(255, 94, 226, 210);
    public static readonly Color Blue = Color.FromArgb(255, 147, 197, 253);
    public static readonly Color Mint = Color.FromArgb(255, 110, 231, 180);
    public static readonly Color Amber = Color.FromArgb(255, 251, 191, 86);
    public static readonly Color Red = Color.FromArgb(255, 248, 113, 113);
    public static readonly Color ListenDot = Color.FromArgb(255, 255, 92, 92);
    public static readonly Color Track = Color.FromArgb(255, 32, 36, 42);
    public static readonly Color HoverFill = Color.FromArgb(28, 255, 255, 255);
    public static readonly Color PressFill = Color.FromArgb(42, 255, 255, 255);

    public const int PillRadiusDip = 18;
    public const int ControlRadiusDip = 11;
    public const int CaptionHeightDip = 64;
    public const int InDurationMs = 320;
    public const int OutDurationMs = 240;

    public static readonly Font Display = Font(16f, FontStyle.Bold);
    public static readonly Font Caption = Font(13.5f, FontStyle.Bold);
    public static readonly Font Section = Font(10.5f, FontStyle.Bold);
    public static readonly Font BodyText = Font(10f);
    public static readonly Font BodyBold = Font(10f, FontStyle.Bold);
    public static readonly Font Small = Font(9.5f);
    public static readonly Font SmallBold = Font(9.5f, FontStyle.Bold);
    public static readonly Font Tiny = Font(8.5f);

    public static Color Accent(OverlayKind kind) => kind switch {
        OverlayKind.Success => Mint,
        OverlayKind.Warning => Amber,
        OverlayKind.Error => Red,
        _ => Blue,
    };

    public static Color PhaseAccent(AppPhase phase, bool loggedIn, bool online) {
        if (!online) {
            return Amber;
        }
        if (!loggedIn) {
            return Red;
        }
        return phase switch {
            AppPhase.Listening => ListenDot,
            AppPhase.Transcribing or AppPhase.ChoosingAction or AppPhase.Generating => Blue,
            AppPhase.Waking or AppPhase.Engaging => Amber,
            _ => Teal,
        };
    }

    public static string PhaseLabel(AppPhase phase, bool loggedIn, bool online) {
        if (!online) {
            return "Offline";
        }
        if (!loggedIn) {
            return "Logged out";
        }
        return phase switch {
            AppPhase.Listening => "Listening",
            AppPhase.Transcribing => "Transcribing",
            AppPhase.ChoosingAction => "Choose an action",
            AppPhase.Generating => "Waiting for reply",
            AppPhase.Waking => "Waking up",
            AppPhase.Engaging => "Starting",
            _ => "Ready",
        };
    }

    public static Font Font(float size, FontStyle style = FontStyle.Regular) {
        try {
            return new Font("Segoe UI Variable Text", size, style, GraphicsUnit.Point);
        } catch (ArgumentException) {
            return new Font("Segoe UI", size, style, GraphicsUnit.Point);
        }
    }

    public static int Dip(Control control, int value) {
        int dpi = control.IsHandleCreated ? control.DeviceDpi : 96;
        return (int)Math.Round(value * dpi / 96.0);
    }

    public static float DipF(Control control, float value) {
        int dpi = control.IsHandleCreated ? control.DeviceDpi : 96;
        return value * dpi / 96f;
    }

    public static void Prepare(Graphics g) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static void DrawPill(Graphics g, Rectangle pill, int radius) {
        using var path = Rounded(pill, radius);
        using var fill = new SolidBrush(Pill);
        using var border = new Pen(Border, 1f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    public static void DrawCard(Graphics g, Rectangle pill, int radius) {
        using var path = Rounded(pill, radius);
        using var fill = new SolidBrush(Card);
        using var border = new Pen(Border, 1f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
    }

    public static void DrawShadow(Graphics g, Rectangle pill, int radius) {
        for (int i = 10; i >= 1; i--) {
            var r = pill;
            r.Inflate(i, i);
            r.Offset(0, i / 2);
            int alpha = Math.Max(4, 16 - i);
            using var path = Rounded(r, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    public static void DrawAccentStripe(Graphics g, Rectangle pill, Color accent, int dip) {
        float pad = Math.Max(2, dip * 14 / 18f);
        var stripe = new RectangleF(pill.Left + dip * 8 / 18f, pill.Top + pad, Math.Max(3, dip * 4 / 18f), pill.Height - pad * 2);
        using var path = Rounded(stripe, Math.Max(1.2f, dip * 2 / 18f));
        using var brush = new SolidBrush(accent);
        g.FillPath(brush, path);
    }

    public static void DrawWave(Graphics g, Rectangle bounds, float[] bars, Color accent, float dip) {
        if (bounds.Width < 16 || bounds.Height < 8 || bars.Length == 0) {
            return;
        }
        float gap = Math.Max(1.4f, 2.2f * dip / 18f);
        float barW = Math.Max(2f * dip / 18f, (bounds.Width - gap * (bars.Length - 1)) / bars.Length);
        float mid = bounds.Top + bounds.Height / 2f;
        float maxH = bounds.Height * 0.48f;
        using var fill = new SolidBrush(Color.FromArgb(230, accent.R, accent.G, accent.B));
        using var dim = new SolidBrush(Color.FromArgb(70, 210, 220, 224));
        for (int i = 0; i < bars.Length; i++) {
            float h = Math.Max(3f * dip / 18f, bars[i] * maxH);
            float x = bounds.Left + i * (barW + gap);
            var rect = new RectangleF(x, mid - h, barW, h * 2);
            using var path = Rounded(rect, barW / 2f);
            g.FillPath(bars[i] > 0.12f ? fill : dim, path);
        }
    }

    public static void StepWave(float[] bars, ref float smoothed, float incoming, float time) {
        incoming = Math.Clamp(incoming, 0, 1);
        smoothed += (incoming - smoothed) * 0.38f;
        float idle = 0.07f + 0.035f * (0.5f + 0.5f * MathF.Sin(time * 2.1f));
        float energy = Math.Max(smoothed, idle);
        int n = bars.Length;
        for (int i = 0; i < n; i++) {
            float t = n == 1 ? 0.5f : i / (float)(n - 1);
            float envelope = 0.42f + 0.58f * MathF.Sin(t * MathF.PI);
            float travel = MathF.Sin(time * 9.5f + i * 0.55f) * 0.18f
                + MathF.Sin(time * 4.2f + i * 1.1f) * 0.10f;
            float target = envelope * energy * (0.78f + travel);
            if (smoothed > 0.08f) {
                float voice = envelope * smoothed * (0.9f + 0.22f * MathF.Sin(time * 17f + i * 0.9f));
                target = Math.Max(target, voice);
            }
            bars[i] += (Math.Clamp(target, 0.04f, 1f) - bars[i]) * 0.42f;
        }
    }

    public static GraphicsPath Rounded(Rectangle rect, int radius) =>
        Rounded(new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), radius);

    public static GraphicsPath Rounded(RectangleF rect, float radius) {
        var path = new GraphicsPath();
        float d = Math.Max(0.1f, radius * 2);
        if (d > rect.Width) {
            d = rect.Width;
        }
        if (d > rect.Height) {
            d = rect.Height;
        }
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static float EaseOutCubic(float t) => 1 - MathF.Pow(1 - t, 3);

    public static float EaseInCubic(float t) => t * t * t;

    public static int Lerp(int a, int b, float t) => a + (int)((b - a) * t);

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static Color Lerp(Color a, Color b, float t) {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static void ApplyChrome(Form form) {
        if (!form.IsHandleCreated) {
            return;
        }
        try {
            int round = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                form.Handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int border = 0x00181412; // COLORREF of (18, 20, 24)
            NativeMethods.DwmSetWindowAttribute(
                form.Handle, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));
            int dark = 1;
            NativeMethods.DwmSetWindowAttribute(
                form.Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        } catch (DllNotFoundException) {
            // Older Windows without dwmapi extras — CS_DROPSHADOW still applies.
        } catch (EntryPointNotFoundException) {
            // Attribute added in a later DWM — ignore.
        }
    }
}
