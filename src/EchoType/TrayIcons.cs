using System.Drawing;
using System.Drawing.Drawing2D;

namespace EchoType;

/// <summary>
/// Tray state icons drawn with GDI+ at runtime (no binary assets) — the NSStatusItem
/// symbol equivalents: loading / idle / waking / listening / transcribing / offline / logged out.
/// </summary>
internal static class TrayIcons {
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Icon> Cache = new();

    public static Icon For(AppPhase phase, bool loggedIn, bool online, bool pageReady) {
        string key = !online ? "offline"
            : !loggedIn ? "loggedout"
            : phase switch {
                AppPhase.Listening => "listening",
                AppPhase.Transcribing or AppPhase.ChoosingAction or AppPhase.Generating => "transcribing",
                AppPhase.Waking or AppPhase.Engaging => "waking",
                _ => pageReady ? "idle" : "loading",
            };
        lock (Gate) {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var icon = Draw(key);
            Cache[key] = icon;
            return icon;
        }
    }

    public static void DisposeAll() {
        lock (Gate) {
            foreach (var icon in Cache.Values) icon.Dispose();
            Cache.Clear();
        }
    }

    private static Icon Draw(string key) {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color color = key switch {
                "loading" => Color.FromArgb(12, 12, 12),
                "idle" => Color.FromArgb(150, 150, 150),
                "waking" => Color.FromArgb(255, 170, 0),
                "listening" => Color.FromArgb(232, 64, 64),
                "transcribing" => Color.FromArgb(64, 132, 240),
                "offline" => Color.FromArgb(255, 140, 0),
                "loggedout" => Color.FromArgb(214, 52, 52),
                _ => Color.Gray,
            };

            if (key == "offline") {
                using var pen = new Pen(color, 2f);
                g.DrawEllipse(pen, 2, 2, 11, 11);
                g.DrawLine(pen, 3, 13, 13, 3);
            } else if (key == "loggedout") {
                using var pen = new Pen(color, 2f);
                using var mark = new SolidBrush(color);
                g.DrawEllipse(pen, 2, 2, 11, 11);
                g.FillRectangle(mark, 7, 4, 2, 5);
                g.FillRectangle(mark, 7, 10, 2, 2);
            } else {
                using var brush = new SolidBrush(color);
                g.FillEllipse(brush, 2, 2, 12, 12);
                if (key == "loading") {
                    // Hairline so a black disc stays visible on a dark taskbar.
                    using var ring = new Pen(Color.FromArgb(210, 210, 210), 1f);
                    g.DrawEllipse(ring, 2, 2, 12, 12);
                }
            }
        }

        // Clone out of the transient HICON so we can destroy the handle immediately.
        IntPtr hIcon = bmp.GetHicon();
        try {
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            return icon;
        } finally {
            Native.NativeMethods.DestroyIcon(hIcon);
        }
    }
}
