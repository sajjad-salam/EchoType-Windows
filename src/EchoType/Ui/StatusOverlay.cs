using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using EchoType.Native;

namespace EchoType.Ui;

internal enum OverlayKind {
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Always-on-top HUD that slides up from the taskbar: a live voice wave while
/// dictating (plus the words so far, for models that stream a live transcript),
/// and short notices that replace Windows balloon tips.
/// </summary>
internal sealed class StatusOverlay : Form {

    private enum Mode {
        Hidden,
        Listening,
        Notice,
    }

    private enum Motion {
        Idle,
        In,
        Out,
    }

    private const int ShadowDip = 22;
    private const int ListenWidthDip = 348;
    private const int ListenHeightDip = 62;
    private const int NoticeWidthDip = 400;
    private const int LiveWidthDip = 440;
    private const int LiveMaxLines = 3;
    private const int BarCount = 32;
    private const int TimerMs = 16;
    private const int NoticeHoldMs = 3400;
    private const int InDurationMs = 320;
    private const int OutDurationMs = 240;

    private static StatusOverlay? _instance;

    private readonly System.Windows.Forms.Timer _tick;
    private readonly float[] _bars = new float[BarCount];
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _listenFont;
    private readonly Font _liveFont;

    private Mode _mode = Mode.Hidden;
    private Motion _motion = Motion.Idle;
    private Func<float>? _level;
    private string _title = "EchoType";
    private string _body = "";
    private string _live = "";
    private string _liveShown = "";
    private bool _liveRtl;
    private OverlayKind _kind = OverlayKind.Info;
    private Rectangle _pill;
    private float _progress;
    private byte _opacity;
    private long _motionStarted;
    private long _noticeShownAt;
    private float _time;
    private float _smoothed;
    private int _restX;
    private int _restY;
    private int _hiddenY;
    private int _targetWidth;
    private int _targetHeight;
    private Bitmap? _canvas;

    public static void ShowListening(string modelName, Func<float> level) {
        try {
            var hud = Instance();
            hud._level = level;
            hud._title = "Listening";
            hud._body = string.IsNullOrWhiteSpace(modelName) ? "" : modelName.Trim();
            hud._live = "";
            hud._liveShown = "";
            hud._kind = OverlayKind.Info;
            hud.Present(Mode.Listening);
        } catch (Exception ex) {
            Log.Write("hud: listening failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Shows the transcript streamed so far under the Listening wave. The pill grows
    /// to fit the last few lines; empty text collapses it back.
    /// </summary>
    public static void SetLiveTranscript(string text) {
        try {
            if (_instance is not { IsDisposed: false } hud || hud._mode != Mode.Listening) {
                return;
            }
            text = (text ?? "").Trim();
            if (text == hud._live) {
                return;
            }
            bool hadLive = hud._live.Length > 0;
            hud._live = text;
            int oldHeight = hud._targetHeight;
            int oldWidth = hud._targetWidth;
            hud.MeasureLayout();
            if (hud._targetHeight != oldHeight || hud._targetWidth != oldWidth || hadLive != text.Length > 0) {
                hud.PlaceAnchor();
                hud.Size = new Size(hud._targetWidth, hud._targetHeight);
                if (hud._motion == Motion.Idle) {
                    hud.Location = new Point(hud._restX, hud._restY);
                }
            }
            hud.PaintFrame();
        } catch (Exception ex) {
            Log.Write("hud: live transcript failed: " + ex.Message);
        }
    }

    public static void HideListening() {
        try {
            if (_instance is not { IsDisposed: false } hud) {
                return;
            }
            if (hud._mode != Mode.Listening) {
                return;
            }
            hud.BeginHide();
        } catch (Exception ex) {
            Log.Write("hud: hide failed: " + ex.Message);
        }
    }

    public static void ShowNotice(string message, OverlayKind kind = OverlayKind.Info, string? title = null) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }
        try {
            var hud = Instance();
            hud._level = null;
            hud._kind = kind;
            hud._title = string.IsNullOrWhiteSpace(title) ? "EchoType" : title.Trim();
            hud._body = message.Trim();
            hud.Present(Mode.Notice);
        } catch (Exception ex) {
            Log.Write("hud: notice failed: " + ex.Message);
        }
    }

    public static void Shutdown() {
        if (_instance is not { IsDisposed: false } hud) {
            return;
        }
        hud._tick.Stop();
        hud.Hide();
        hud.Dispose();
        _instance = null;
    }

    private static StatusOverlay Instance() {
        if (_instance is { IsDisposed: false } existing) {
            return existing;
        }
        _instance = new StatusOverlay();
        return _instance;
    }

    private StatusOverlay() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Size = new Size(ListenWidthDip + ShadowDip * 2, ListenHeightDip + ShadowDip * 2);
        _ = Handle;

        _listenFont = HudTheme.Font(11.5f, FontStyle.Bold);
        _liveFont = HudTheme.Font(10.5f);
        _titleFont = HudTheme.Font(10.5f, FontStyle.Bold);
        _bodyFont = HudTheme.Font(10f);

        _tick = new System.Windows.Forms.Timer { Interval = TimerMs };
        _tick.Tick += (_, _) => OnTick();

        Click += (_, _) => {
            if (_mode == Mode.Notice) {
                BeginHide();
            }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_TOPMOST
                | NativeMethods.WS_EX_LAYERED);
            return cp;
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == NativeMethods.WM_NCHITTEST && _mode == Mode.Listening) {
            m.Result = (IntPtr)NativeMethods.HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _tick.Stop();
            _tick.Dispose();
            _listenFont.Dispose();
            _liveFont.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _canvas?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Present(Mode mode) {
        try {
            bool wasVisible = _mode != Mode.Hidden && _motion != Motion.Out;
            _mode = mode;
            MeasureLayout();
            PlaceAnchor();
            Array.Clear(_bars);
            _smoothed = 0;
            _noticeShownAt = mode == Mode.Notice ? Environment.TickCount64 : 0;

            if (!Visible) {
                Location = new Point(_restX, _hiddenY);
                Size = new Size(_targetWidth, _targetHeight);
                Show();
                NativeMethods.SetWindowPos(
                    Handle, NativeMethods.HWND_TOPMOST,
                    Left, Top, Width, Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            } else {
                Size = new Size(_targetWidth, _targetHeight);
            }

            if (wasVisible && _motion != Motion.Out) {
                _motion = Motion.Idle;
                _progress = 1;
                _opacity = 255;
                Location = new Point(_restX, _restY);
            } else {
                _motion = Motion.In;
                _progress = 0;
                _opacity = 0;
                _motionStarted = Environment.TickCount64;
                Location = new Point(_restX, _hiddenY);
            }
            _tick.Start();
            PaintFrame();
        } catch (Exception ex) {
            Log.Write("hud: show failed: " + ex.Message);
        }
    }

    private void BeginHide() {
        if (_mode == Mode.Hidden) {
            return;
        }
        if (_motion == Motion.Out) {
            return;
        }
        _motion = Motion.Out;
        _progress = 0;
        _motionStarted = Environment.TickCount64;
        _tick.Start();
    }

    private void FinishHide() {
        _mode = Mode.Hidden;
        _motion = Motion.Idle;
        _level = null;
        _tick.Stop();
        Hide();
    }

    private void OnTick() {
        if (IsDisposed) {
            return;
        }
        _time += TimerMs / 1000f;
        long now = Environment.TickCount64;

        if (_motion != Motion.Idle) {
            int duration = _motion == Motion.In ? InDurationMs : OutDurationMs;
            _progress = Math.Clamp((now - _motionStarted) / (float)duration, 0, 1);
            float eased = _motion == Motion.In ? HudTheme.EaseOutCubic(_progress) : HudTheme.EaseInCubic(_progress);
            int y = _motion == Motion.In
                ? HudTheme.Lerp(_hiddenY, _restY, eased)
                : HudTheme.Lerp(_restY, _hiddenY, eased);
            _opacity = (byte)Math.Clamp(
                (_motion == Motion.In ? eased : 1 - eased) * 255, 0, 255);
            Location = new Point(_restX, y);
            if (_progress >= 1) {
                if (_motion == Motion.Out) {
                    FinishHide();
                    return;
                }
                _motion = Motion.Idle;
                _opacity = 255;
                Location = new Point(_restX, _restY);
            }
        } else {
            _opacity = 255;
            if (_mode == Mode.Notice && _noticeShownAt > 0 && now - _noticeShownAt >= NoticeHoldMs) {
                BeginHide();
            }
        }

        if (_mode == Mode.Listening) {
            StepWave();
        }
        PaintFrame();
    }

    private void MeasureLayout() {
        int shadow = Dip(ShadowDip);
        if (_mode == Mode.Listening) {
            int w = Dip(_live.Length > 0 ? LiveWidthDip : ListenWidthDip);
            int h = Dip(ListenHeightDip);
            _liveShown = "";
            if (_live.Length > 0) {
                int textW = w - Dip(44);
                _liveRtl = StartsRightToLeft(_live);
                _liveShown = TailThatFits(_live, textW);
                Size size = TextRenderer.MeasureText(_liveShown, _liveFont, new Size(textW, Dip(400)), LiveFlags());
                h += size.Height + Dip(12);
            }
            _targetWidth = w + shadow * 2;
            _targetHeight = h + shadow * 2;
            _pill = new Rectangle(shadow, shadow, w, h);
            return;
        }

        int maxW = Dip(NoticeWidthDip);
        int textWidth = maxW - Dip(44);
        var proposed = new Size(textWidth, Dip(400));
        Size titleSize = TextRenderer.MeasureText(_title, _titleFont, proposed, TextFormatFlags.NoPadding);
        Size bodySize = TextRenderer.MeasureText(_body, _bodyFont, proposed,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        int contentH = Math.Max(Dip(44), Dip(14) + titleSize.Height + Dip(4) + bodySize.Height + Dip(14));
        int contentW = Math.Clamp(Math.Max(titleSize.Width, bodySize.Width) + Dip(44), Dip(280), maxW);
        _targetWidth = contentW + shadow * 2;
        _targetHeight = contentH + shadow * 2;
        _pill = new Rectangle(shadow, shadow, contentW, contentH);
    }

    private void PlaceAnchor() {
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        _restX = area.Left + (area.Width - _targetWidth) / 2;

        bool taskbarBottom = area.Bottom < bounds.Bottom - 2;
        bool taskbarTop = area.Top > bounds.Top + 2;
        if (taskbarTop && !taskbarBottom) {
            _restY = area.Top + Dip(10);
            _hiddenY = bounds.Top - _targetHeight + Dip(8);
        } else {
            _restY = area.Bottom - _targetHeight + Dip(6);
            _hiddenY = bounds.Bottom - Dip(ShadowDip) + Dip(4);
        }
    }

    private TextFormatFlags LiveFlags() =>
        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding
        | (_liveRtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left);

    /// <summary>The latest words of <paramref name="text"/> that fit in <see cref="LiveMaxLines"/> lines.</summary>
    private string TailThatFits(string text, int width) {
        int lineHeight = TextRenderer.MeasureText("Ag", _liveFont, new Size(width, Dip(400)), LiveFlags()).Height;
        int maxHeight = lineHeight * LiveMaxLines + 2;
        var proposed = new Size(width, Dip(800));
        if (TextRenderer.MeasureText(text, _liveFont, proposed, LiveFlags()).Height <= maxHeight) {
            return text;
        }
        // Drop words from the front until the tail fits; start near the end so long
        // dictations stay cheap.
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int start = Math.Max(0, words.Length - 60);
        string candidate = text;
        for (; start < words.Length; start++) {
            candidate = "… " + string.Join(' ', words, start, words.Length - start);
            if (TextRenderer.MeasureText(candidate, _liveFont, proposed, LiveFlags()).Height <= maxHeight) {
                return candidate;
            }
        }
        return candidate;
    }

    private static bool StartsRightToLeft(string text) {
        foreach (char c in text) {
            if (c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC') {
                return true;
            }
            if (char.IsLetter(c)) {
                return false;
            }
        }
        return false;
    }

    private void StepWave() {
        float incoming = 0;
        try {
            incoming = _level?.Invoke() ?? 0;
        } catch {
            incoming = 0;
        }
        HudTheme.StepWave(_bars, ref _smoothed, incoming, _time);
    }

    private void PaintFrame() {
        if (!IsHandleCreated || IsDisposed || !Visible || _targetWidth <= 0 || _targetHeight <= 0) {
            return;
        }
        if (_canvas == null || _canvas.Width != _targetWidth || _canvas.Height != _targetHeight) {
            _canvas?.Dispose();
            _canvas = new Bitmap(_targetWidth, _targetHeight, PixelFormat.Format32bppArgb);
        }
        using (var g = Graphics.FromImage(_canvas)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.Transparent);
            int radius = Dip(HudTheme.PillRadiusDip);
            HudTheme.DrawShadow(g, _pill, radius);
            HudTheme.DrawPill(g, _pill, radius);
            if (_mode == Mode.Listening) {
                DrawListening(g, _pill);
            } else if (_mode == Mode.Notice) {
                DrawNotice(g, _pill);
            }
        }
        try {
            LayeredSurface.Present(this, _canvas, _opacity);
        } catch (Exception ex) {
            Log.Write("hud: present failed: " + ex.Message);
        }
    }

    private void DrawListening(Graphics g, Rectangle pill) {
        if (_liveShown.Length > 0) {
            var header = new Rectangle(pill.Left, pill.Top, pill.Width, Dip(ListenHeightDip));
            DrawListeningHeader(g, header);
            using (var rule = new Pen(Color.FromArgb(40, 255, 255, 255))) {
                g.DrawLine(rule, pill.Left + Dip(18), header.Bottom - Dip(4), pill.Right - Dip(18), header.Bottom - Dip(4));
            }
            var textRect = new Rectangle(
                pill.Left + Dip(22),
                header.Bottom + Dip(2),
                pill.Width - Dip(44),
                pill.Bottom - header.Bottom - Dip(10));
            TextRenderer.DrawText(g, _liveShown, _liveFont, textRect, HudTheme.Body, LiveFlags());
            return;
        }
        DrawListeningHeader(g, pill);
    }

    private void DrawListeningHeader(Graphics g, Rectangle pill) {
        int pad = Dip(18);
        int cx = pill.Left + pad + Dip(5);
        int cy = pill.Top + pill.Height / 2;
        float pulse = 0.55f + 0.45f * MathF.Sin(_time * 5.5f);
        float glow = Dip(6) + Dip(3) * pulse + Dip(6) * _smoothed * pulse;
        using (var glowBrush = new SolidBrush(Color.FromArgb((int)(40 + 50 * _smoothed), 255, 88, 88))) {
            g.FillEllipse(glowBrush, cx - glow, cy - glow, glow * 2, glow * 2);
        }
        using (var dot = new SolidBrush(HudTheme.ListenDot)) {
            float d = DipF(4.5f);
            g.FillEllipse(dot, cx - d, cy - d, d * 2, d * 2);
        }

        int waveWidth = Dip(168);
        var wave = new Rectangle(
            pill.Right - pad - waveWidth,
            pill.Top + Dip(14),
            waveWidth,
            pill.Height - Dip(28));
        var labelBounds = new Rectangle(
            pill.Left + pad + Dip(18),
            pill.Top,
            Math.Max(Dip(80), wave.Left - (pill.Left + pad + Dip(26))),
            pill.Height);
        TextRenderer.DrawText(g, "Listening", _listenFont, labelBounds,
            HudTheme.Body,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        HudTheme.DrawWave(g, wave, _bars, HudTheme.Teal, Dip(18));
    }

    private void DrawNotice(Graphics g, Rectangle pill) {
        Color accent = HudTheme.Accent(_kind);
        HudTheme.DrawAccentStripe(g, pill, accent, Dip(18));

        int left = pill.Left + Dip(22);
        int top = pill.Top + Dip(13);
        int textW = pill.Width - Dip(36);
        TextRenderer.DrawText(g, _title, _titleFont,
            new Rectangle(left, top, textW, Dip(20)),
            HudTheme.Title,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, _body, _bodyFont,
            new Rectangle(left, top + Dip(20), textW, pill.Height - Dip(36)),
            HudTheme.Body,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    private int Dip(int value) {
        int dpi = IsHandleCreated ? DeviceDpi : 96;
        return (int)Math.Round(value * dpi / 96.0);
    }

    private float DipF(float value) {
        int dpi = IsHandleCreated ? DeviceDpi : 96;
        return value * dpi / 96f;
    }

}
