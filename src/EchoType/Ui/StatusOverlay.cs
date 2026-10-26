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

/// <summary>What the progress pill is waiting on; picks its words and pacing.</summary>
internal enum WorkStage {
    Transcribing,
    Thinking,
    Cleaning,
    Translating,
}

/// <summary>
/// Always-on-top HUD that slides up from the taskbar: a live voice-spectrum wave while
/// dictating (plus the words so far, for models that stream a live transcript),
/// a slim progress pill while the transcript or reply is on its way, and short
/// notices that replace Windows balloon tips. The listening and progress pills have
/// an ✕ button that cancels the task; clicking it never takes focus from the app the
/// result will be pasted into.
/// </summary>
internal sealed class StatusOverlay : Form {

    private enum Mode {
        Hidden,
        Listening,
        Working,
        Notice,
    }

    private enum Motion {
        Idle,
        In,
        Out,
    }

    private const int ShadowDip = 22;
    private const int ListenWidthDip = 392;
    private const int ListenHeightDip = 62;
    private const int NoticeWidthDip = 400;
    private const int LiveWidthDip = 470;
    private const int LiveMaxLines = 3;
    private const int BarCount = 32;
    private const int CloseSizeDip = 28;
    private const int TimerMs = 16;
    private const int NoticeHoldMs = 3400;
    private const int InDurationMs = 320;
    private const int OutDurationMs = 240;
    private const int WorkWidthDip = 304;
    private const int WorkHeightDip = 48;
    private const int MorphDurationMs = 220;
    private const int FinishHoldMs = 260;

    private static StatusOverlay? _instance;

    /// <summary>Raised on the UI thread when the ✕ on the listening or progress pill is clicked.</summary>
    public static event Action? CancelClicked;

    private readonly System.Windows.Forms.Timer _tick;
    private readonly float[] _bars = new float[BarCount];
    private readonly float[] _spectrumFrame = new float[BarCount];
    private readonly Font _titleFont;
    private readonly Font _bodyFont;
    private readonly Font _listenFont;
    private readonly Font _detailFont;
    private Font _liveFont;

    private Mode _mode = Mode.Hidden;
    private Motion _motion = Motion.Idle;
    private Func<float>? _level;
    private Func<float[], bool>? _spectrum;
    private string _title = "EchoType";
    private string _body = "";
    private string _live = "";
    private string _liveShown = "";
    private bool _liveRtl;
    private OverlayKind _kind = OverlayKind.Info;
    private Rectangle _pill;
    private Rectangle _close;
    private bool _closeHover;
    private bool _closePressed;
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

    // Working (progress pill). The real duration is unknown, so the bar eases toward a
    // per-stage cap and only reaches 100% when the work is actually done.
    private string[] _workWords = [];
    private float _workProgress;
    private float _workStageFrom;
    private float _workCap;
    private float _workTau = 2f;
    private bool _workFinishing;
    private long _workFinishedAt;
    private long _workLastTick;

    // Size morph between modes (listening → progress → notice) so the pill reshapes in place.
    private bool _morphing;
    private Size _morphFrom;
    private Size _morphTo;
    private long _morphStarted;
    private bool _anchorTop;

    /// <param name="spectrum">
    /// Fills one 0–1 value per bar (low to high frequency) and returns true when a real
    /// voice spectrum is available; otherwise the bars fall back to <paramref name="level"/>.
    /// </param>
    public static void ShowListening(string modelName, Func<float> level, Func<float[], bool>? spectrum = null) {
        try {
            var hud = Instance();
            hud._level = level;
            hud._spectrum = spectrum;
            hud._title = Loc.T("Listening");
            hud._body = string.IsNullOrWhiteSpace(modelName) ? "" : modelName.Trim();
            hud.SetLive("");
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
            hud.SetLive(text);
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

    /// <summary>
    /// Shows (or advances) the progress pill. <paramref name="cap"/> is how far the bar may
    /// fill before this stage finishes; <paramref name="restart"/> starts a new run from 0,
    /// otherwise the bar continues from where the previous stage left it.
    /// </summary>
    public static void ShowWorking(WorkStage stage, string detail, float cap, bool restart) {
        try {
            var hud = Instance();
            if (restart) {
                hud._workProgress = 0;
            }
            hud._level = null;
            hud._spectrum = null;
            hud._kind = OverlayKind.Info;
            hud._body = string.IsNullOrWhiteSpace(detail) ? "" : detail.Trim();
            hud._workWords = (stage switch {
                WorkStage.Transcribing => new[] { "Processing…", "Transcribing…", "Finishing up…" },
                WorkStage.Cleaning => ["Cleaning up…", "Polishing…", "Almost there…"],
                WorkStage.Translating => ["Translating…", "Almost there…"],
                _ => ["Thinking…", "Writing…", "Almost there…"],
            }).Select(Loc.T).ToArray();
            hud._workTau = stage switch {
                WorkStage.Transcribing => 1.8f,
                WorkStage.Translating => 1.4f,
                WorkStage.Cleaning => 4f,
                _ => 6f,
            };
            hud._workStageFrom = hud._workProgress;
            hud._workCap = Math.Clamp(Math.Max(cap, hud._workProgress + 0.05f), 0.05f, 0.97f);
            hud._workFinishing = false;
            hud._workFinishedAt = 0;
            hud._workLastTick = 0;
            if (hud._mode == Mode.Working && hud._motion != Motion.Out) {
                hud.PaintFrame();
                return;
            }
            hud.Present(Mode.Working);
        } catch (Exception ex) {
            Log.Write("hud: working failed: " + ex.Message);
        }
    }

    /// <summary>The work finished: fill the bar to 100%, then slide away.</summary>
    public static void CompleteWorking() {
        try {
            if (_instance is not { IsDisposed: false } hud || hud._mode != Mode.Working
                || hud._motion == Motion.Out || hud._workFinishing) {
                return;
            }
            hud._workFinishing = true;
            hud._workFinishedAt = 0;
            hud._tick.Start();
        } catch (Exception ex) {
            Log.Write("hud: complete failed: " + ex.Message);
        }
    }

    /// <summary>Cancelled, or paused while the action picker is up: slide away without filling.</summary>
    public static void HideWorking() {
        try {
            if (_instance is { IsDisposed: false } hud && hud._mode == Mode.Working) {
                hud.BeginHide();
            }
        } catch (Exception ex) {
            Log.Write("hud: hide working failed: " + ex.Message);
        }
    }

    public static void ShowNotice(string message, OverlayKind kind = OverlayKind.Info, string? title = null) {
        if (string.IsNullOrWhiteSpace(message)) {
            return;
        }
        try {
            var hud = Instance();
            hud._level = null;
            hud._spectrum = null;
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
        _detailFont = HudTheme.Font(9f);

        _tick = new System.Windows.Forms.Timer { Interval = TimerMs };
        _tick.Tick += (_, _) => OnTick();

        MouseDown += (_, e) => {
            if (e.Button == MouseButtons.Left && IsCancelable && _close.Contains(e.Location)) {
                _closePressed = true;
                PaintFrame();
            }
        };
        MouseUp += (_, e) => {
            bool fire = _closePressed && IsCancelable && _close.Contains(e.Location);
            _closePressed = false;
            PaintFrame();
            if (!fire) {
                return;
            }
            Log.Write("hud: cancel clicked");
            try {
                CancelClicked?.Invoke();
            } catch (Exception ex) {
                Log.Write("hud: cancel handler failed: " + ex.Message);
            }
        };
        Click += (_, _) => {
            if (_mode == Mode.Notice) {
                BeginHide();
            }
        };
    }

    /// <summary>The ✕ is live: listening or progress pill, settled, not finishing.</summary>
    private bool IsCancelable =>
        _mode is Mode.Listening or Mode.Working
        && _motion != Motion.Out
        && !_morphing
        && !_workFinishing
        && !_close.IsEmpty;

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
        if (m.Msg == NativeMethods.WM_MOUSEACTIVATE) {
            // Clicking the ✕ must not take focus from the field the reply goes into.
            m.Result = (IntPtr)NativeMethods.MA_NOACTIVATE;
            return;
        }
        if (m.Msg == NativeMethods.WM_NCHITTEST && _mode is Mode.Listening or Mode.Working) {
            // Only the ✕ takes clicks; the rest of the pill lets the mouse fall through.
            long lp = m.LParam.ToInt64();
            var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            m.Result = (IntPtr)(IsCancelable && _close.Contains(pt)
                ? NativeMethods.HTCLIENT
                : NativeMethods.HTTRANSPARENT);
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
            _detailFont.Dispose();
            _canvas?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Stores the live text and switches to IBM Plex Arabic when it is Arabic.</summary>
    private void SetLive(string text) {
        _live = text;
        if (text.Length == 0) {
            return;
        }
        bool wantArabic = ArabicFont.HasArabic(text);
        bool haveArabic = ArabicFont.Family is { } family && _liveFont.FontFamily.Name == family.Name;
        if (wantArabic != haveArabic) {
            var next = HudTheme.ContentFont(10.5f, text);
            _liveFont.Dispose();
            _liveFont = next;
        }
    }

    private void Present(Mode mode) {
        try {
            bool wasVisible = _mode != Mode.Hidden && _motion != Motion.Out;
            Size oldPill = _pill.Size;
            bool morph = wasVisible && Visible && _motion == Motion.Idle && _mode != mode;
            _morphing = false;
            _mode = mode;
            _closePressed = false;
            MeasureLayout();
            PlaceAnchor();
            Array.Clear(_bars);
            _smoothed = 0;
            _noticeShownAt = mode == Mode.Notice ? Environment.TickCount64 : 0;
            morph = morph && oldPill.Width > 0 && oldPill != _pill.Size;

            if (!Visible) {
                Location = new Point(_restX, _hiddenY);
                Size = new Size(_targetWidth, _targetHeight);
                Show();
                NativeMethods.SetWindowPos(
                    Handle, NativeMethods.HWND_TOPMOST,
                    Left, Top, Width, Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            } else if (!morph) {
                Size = new Size(_targetWidth, _targetHeight);
            }

            if (wasVisible && _motion != Motion.Out) {
                _motion = Motion.Idle;
                _progress = 1;
                _opacity = 255;
                if (morph) {
                    StartMorph(oldPill);
                } else {
                    Location = new Point(_restX, _restY);
                }
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
        if (_morphing) {
            EndMorph();
        }
        _motion = Motion.Out;
        _progress = 0;
        _motionStarted = Environment.TickCount64;
        _tick.Start();
    }

    private void FinishHide() {
        _morphing = false;
        _workFinishing = false;
        _mode = Mode.Hidden;
        _motion = Motion.Idle;
        _level = null;
        _spectrum = null;
        _closeHover = false;
        _closePressed = false;
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

        if (_morphing) {
            float t = Math.Clamp((now - _morphStarted) / (float)MorphDurationMs, 0, 1);
            if (t >= 1) {
                EndMorph();
            } else {
                SetMorphPill(HudTheme.EaseOutCubic(t));
            }
        }

        // Hover is polled: outside the ✕ the window is click-through, so it gets no mouse-leave.
        bool hover = IsCancelable && _close.Contains(PointToClient(Cursor.Position));
        if (hover != _closeHover) {
            _closeHover = hover;
            if (!hover) {
                _closePressed = false;
            }
        }

        if (_mode == Mode.Listening) {
            StepWave();
        } else if (_mode == Mode.Working) {
            StepWork(now);
        }
        PaintFrame();
    }

    private void StepWork(long now) {
        if (_workFinishing) {
            _workProgress = Math.Min(1f, _workProgress + (1.02f - _workProgress) * 0.3f);
            if (_workProgress >= 1f) {
                if (_workFinishedAt == 0) {
                    _workFinishedAt = now;
                } else if (now - _workFinishedAt >= FinishHoldMs && _motion == Motion.Idle) {
                    BeginHide();
                }
            }
            return;
        }
        if (_motion == Motion.Out) {
            return;
        }
        // Ease toward the stage cap (quick at first, slower as it nears it) plus a slow
        // creep so a long wait never looks frozen. Only CompleteWorking fills it to 100%.
        // The WinForms timer ticks slower than its interval; advance by real elapsed time.
        float dt = _workLastTick == 0 ? TimerMs / 1000f : Math.Clamp((now - _workLastTick) / 1000f, 0, 0.25f);
        _workLastTick = now;
        float ease = Math.Max(0, (_workCap - _workProgress) * (1 - MathF.Exp(-dt / _workTau)));
        _workProgress = Math.Min(0.97f, _workProgress + ease + 0.002f * dt);
    }

    private string WorkLabel() {
        if (_workFinishing) {
            return Loc.T("Done");
        }
        if (_workWords.Length == 0) {
            return Loc.T("Working…");
        }
        float span = Math.Max(0.01f, _workCap - _workStageFrom);
        float f = Math.Clamp((_workProgress - _workStageFrom) / span, 0, 0.999f);
        return _workWords[Math.Min(_workWords.Length - 1, (int)(f * _workWords.Length))];
    }

    private void StartMorph(Size from) {
        int shadow = Dip(ShadowDip);
        _morphFrom = from;
        _morphTo = _pill.Size;
        _morphStarted = Environment.TickCount64;
        _morphing = true;
        // The window covers both shapes while the pill animates between them.
        _targetWidth = Math.Max(from.Width, _morphTo.Width) + shadow * 2;
        _targetHeight = Math.Max(from.Height, _morphTo.Height) + shadow * 2;
        PlaceAnchor();
        Bounds = new Rectangle(_restX, _restY, _targetWidth, _targetHeight);
        SetMorphPill(0);
    }

    private void SetMorphPill(float t) {
        int shadow = Dip(ShadowDip);
        int w = HudTheme.Lerp(_morphFrom.Width, _morphTo.Width, t);
        int h = HudTheme.Lerp(_morphFrom.Height, _morphTo.Height, t);
        int y = _anchorTop ? shadow : _targetHeight - shadow - h;
        _pill = new Rectangle((_targetWidth - w) / 2, y, w, h);
    }

    private void EndMorph() {
        _morphing = false;
        MeasureLayout();
        PlaceAnchor();
        Bounds = new Rectangle(_restX, _restY, _targetWidth, _targetHeight);
    }

    private void MeasureLayout() {
        int shadow = Dip(ShadowDip);
        _close = Rectangle.Empty;
        if (_mode == Mode.Listening) {
            int w = Dip(_live.Length > 0 ? LiveWidthDip : ListenWidthDip);
            int h = Dip(ListenHeightDip);
            _liveShown = "";
            if (_live.Length > 0) {
                int textW = w - Dip(44);
                _liveRtl = ArabicFont.StartsRightToLeft(_live);
                _liveShown = TailThatFits(_live, textW);
                Size size = TextRenderer.MeasureText(_liveShown, _liveFont, new Size(textW, Dip(400)), LiveFlags());
                h += size.Height + Dip(12);
            }
            _targetWidth = w + shadow * 2;
            _targetHeight = h + shadow * 2;
            _pill = new Rectangle(shadow, shadow, w, h);
            _close = CloseRect(Dip(ListenHeightDip));
            return;
        }

        if (_mode == Mode.Working) {
            int w = Dip(WorkWidthDip);
            int h = Dip(WorkHeightDip);
            _targetWidth = w + shadow * 2;
            _targetHeight = h + shadow * 2;
            _pill = new Rectangle(shadow, shadow, w, h);
            _close = CloseRect(h);
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

    /// <summary>The ✕ button, centred in the first <paramref name="rowHeight"/> of the pill, at the trailing edge.</summary>
    private Rectangle CloseRect(int rowHeight) {
        int size = Dip(CloseSizeDip);
        if (rowHeight < size + Dip(8)) {
            size = Math.Max(Dip(20), rowHeight - Dip(16));
        }
        return MirrorInPill(new Rectangle(
            _pill.Right - Dip(12) - size,
            _pill.Top + (rowHeight - size) / 2,
            size,
            size));
    }

    private void PlaceAnchor() {
        var screen = Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        _restX = area.Left + (area.Width - _targetWidth) / 2;

        bool taskbarBottom = area.Bottom < bounds.Bottom - 2;
        bool taskbarTop = area.Top > bounds.Top + 2;
        _anchorTop = taskbarTop && !taskbarBottom;
        if (_anchorTop) {
            _restY = area.Top + Dip(10);
            _hiddenY = bounds.Top - _targetHeight + Dip(8);
        } else {
            _restY = area.Bottom - _targetHeight + Dip(6);
            _hiddenY = bounds.Bottom - Dip(ShadowDip) + Dip(4);
        }
    }

    /// <summary>Mirrors a rectangle inside the pill for the Arabic (right-to-left) layout.</summary>
    private Rectangle MirrorInPill(Rectangle r) =>
        Loc.Rtl ? new Rectangle(_pill.Left + _pill.Right - r.Right, r.Y, r.Width, r.Height) : r;

    private RectangleF MirrorInPill(RectangleF r) =>
        Loc.Rtl ? new RectangleF(_pill.Left + _pill.Right - r.Right, r.Y, r.Width, r.Height) : r;

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

    private void StepWave() {
        float incoming;
        try {
            incoming = _level?.Invoke() ?? 0;
        } catch {
            incoming = 0;
        }
        bool haveSpectrum;
        try {
            haveSpectrum = _spectrum?.Invoke(_spectrumFrame) ?? false;
        } catch {
            haveSpectrum = false;
        }
        if (haveSpectrum) {
            HudTheme.StepSpectrum(_bars, ref _smoothed, _spectrumFrame, incoming);
        } else {
            HudTheme.StepWave(_bars, ref _smoothed, incoming, _time);
        }
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
            if (_morphing) {
                // Content appears once the pill has settled into its new shape.
            } else if (_mode == Mode.Listening) {
                DrawListening(g, _pill);
                DrawCloseButton(g);
            } else if (_mode == Mode.Working) {
                DrawWorking(g, _pill);
                if (!_workFinishing) {
                    DrawCloseButton(g);
                }
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
        var dotBox = MirrorInPill(new Rectangle(pill.Left + pad, pill.Top + pill.Height / 2 - Dip(5), Dip(10), Dip(10)));
        float cx = dotBox.Left + dotBox.Width / 2f;
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

        // [dot] Listening  [voice spectrum]  [✕]  — mirrored for Arabic.
        int waveWidth = Dip(172);
        int waveRight = pill.Right - Dip(12) - Dip(CloseSizeDip) - Dip(12);
        var wave = MirrorInPill(new Rectangle(
            waveRight - waveWidth,
            pill.Top + Dip(14),
            waveWidth,
            pill.Height - Dip(28)));
        int labelLeft = pill.Left + pad + Dip(18);
        var labelBounds = MirrorInPill(new Rectangle(
            labelLeft,
            pill.Top,
            Math.Max(Dip(60), waveRight - waveWidth - Dip(8) - labelLeft),
            pill.Height));
        TextRenderer.DrawText(g, _title, _listenFont, labelBounds,
            HudTheme.Body,
            Loc.Flags(TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));
        HudTheme.DrawWave(g, wave, _bars, Color.White, Dip(18));
    }

    private void DrawCloseButton(Graphics g) {
        if (_close.IsEmpty) {
            return;
        }
        var r = new RectangleF(_close.X + 0.5f, _close.Y + 0.5f, _close.Width - 1, _close.Height - 1);
        Color fill = _closePressed ? Color.FromArgb(90, HudTheme.Red)
            : _closeHover ? Color.FromArgb(56, HudTheme.Red)
            : Color.FromArgb(24, 255, 255, 255);
        using (var brush = new SolidBrush(fill)) {
            g.FillEllipse(brush, r);
        }
        using (var ring = new Pen(_closeHover ? Color.FromArgb(150, HudTheme.Red) : HudTheme.Border, 1f)) {
            g.DrawEllipse(ring, r);
        }
        float s = r.Width * 0.17f;
        float cx = r.Left + r.Width / 2f;
        float cy = r.Top + r.Height / 2f;
        using var pen = new Pen(_closeHover ? HudTheme.Red : HudTheme.Title, DipF(1.6f)) {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
        g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
    }

    private void DrawWorking(Graphics g, Rectangle pill) {
        int pad = Dip(20);
        // Leave the trailing edge for the ✕.
        int contentW = pill.Width - pad - Dip(12) - Dip(CloseSizeDip) - Dip(12);
        var textRow = MirrorInPill(new Rectangle(pill.Left + pad, pill.Top + Dip(8), contentW, Dip(22)));
        if (_body.Length > 0) {
            TextRenderer.DrawText(g, _body, _detailFont, textRow, HudTheme.Muted,
                Loc.Flags(TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding));
        }
        TextRenderer.DrawText(g, WorkLabel(), _titleFont, textRow,
            _workFinishing ? HudTheme.Mint : HudTheme.Body,
            Loc.Flags(TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding));

        float barH = DipF(4f);
        var track = MirrorInPill(new RectangleF(pill.Left + pad, pill.Bottom - Dip(13) - barH, contentW, barH));
        using (var trackPath = HudTheme.Rounded(track, barH / 2))
        using (var trackBrush = new SolidBrush(Color.FromArgb(34, 255, 255, 255))) {
            g.FillPath(trackBrush, trackPath);
        }

        // Arabic fills from the right.
        float fillW = Math.Max(barH, track.Width * Math.Clamp(_workProgress, 0, 1));
        var fill = Loc.Rtl
            ? new RectangleF(track.Right - fillW, track.Top, fillW, barH)
            : new RectangleF(track.Left, track.Top, fillW, barH);
        Color end = _workFinishing ? HudTheme.Mint : HudTheme.Teal;
        using var fillPath = HudTheme.Rounded(fill, barH / 2);
        using (var fillBrush = new LinearGradientBrush(
            new RectangleF(track.Left - 1, track.Top, track.Width + 2, barH),
            Loc.Rtl ? end : HudTheme.Blue, Loc.Rtl ? HudTheme.Blue : end, LinearGradientMode.Horizontal)) {
            g.FillPath(fillBrush, fillPath);
        }

        // A soft highlight sweeps along the filled part so a slow stretch still reads as moving.
        if (!_workFinishing && fillW > barH * 3) {
            float band = Math.Max(DipF(36), fillW * 0.35f);
            float travel = (_time % 1.6f) / 1.6f * (fillW + band);
            float x = Loc.Rtl ? fill.Right - travel : fill.Left - band + travel;
            var sweep = new RectangleF(x, fill.Top, band, barH);
            using var sweepBrush = new LinearGradientBrush(sweep, Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal) {
                InterpolationColors = new ColorBlend {
                    Colors = [Color.FromArgb(0, 255, 255, 255), Color.FromArgb(120, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)],
                    Positions = [0f, 0.5f, 1f],
                },
            };
            var clip = g.Clip;
            g.SetClip(fillPath, CombineMode.Intersect);
            g.FillRectangle(sweepBrush, sweep);
            g.Clip = clip;
        }
    }

    private void DrawNotice(Graphics g, Rectangle pill) {
        Color accent = HudTheme.Accent(_kind);
        if (Loc.Rtl) {
            // Accent stripe on the leading (right) edge for right-to-left reading.
            var saved = g.Transform;
            using (var flip = new Matrix(-1, 0, 0, 1, pill.Left * 2 + pill.Width, 0)) {
                g.Transform = flip;
                HudTheme.DrawAccentStripe(g, pill, accent, Dip(18));
            }
            g.Transform = saved;
        } else {
            HudTheme.DrawAccentStripe(g, pill, accent, Dip(18));
        }

        int top = pill.Top + Dip(13);
        int textW = pill.Width - Dip(36);
        var titleRect = MirrorInPill(new Rectangle(pill.Left + Dip(22), top, textW - Dip(6), Dip(20)));
        var bodyRect = MirrorInPill(new Rectangle(pill.Left + Dip(22), top + Dip(20), textW - Dip(6), pill.Height - Dip(36)));
        bool bodyRtl = Loc.Rtl || ArabicFont.StartsRightToLeft(_body);
        TextRenderer.DrawText(g, _title, _titleFont, titleRect,
            HudTheme.Title,
            Loc.Flags(TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));
        TextRenderer.DrawText(g, _body, _bodyFont, bodyRect,
            HudTheme.Body,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding
            | (bodyRtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left));
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
