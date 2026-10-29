using EchoType.Native;
using EchoType.Tasks;

namespace EchoType.Ui;

/// <summary>
/// A task reminder card in the bottom corner of the screen. Unlike the HUD notices it stays
/// until you act on it — Done, Snooze, Stop reminder or Delete — and never takes focus from
/// what you are typing in. Several reminders stack upwards.
/// </summary>
internal sealed class ReminderToast : Form {

    private const int WidthDip = 400;
    private static readonly List<ReminderToast> Open = [];

    private readonly string _noticeId;
    private readonly Action<NoticeAction> _act;
    private readonly string _title;
    private readonly string _detail;
    private readonly bool _late;
    private readonly HudButton _done;
    private readonly HudButton _snooze;
    private readonly HudButton _stop;
    private readonly HudButton _more;
    private readonly ContextMenuStrip _snoozeMenu;
    private readonly ContextMenuStrip _moreMenu;
    private readonly Font _titleFont;
    private readonly Font _detailFont;
    private bool _acted;

    public static int Count => Open.Count;

    /// <summary>Shows (or refreshes) the card for a reminder.</summary>
    public static void Show(Notice notice, TaskItem? item, IReadOnlyList<string> projects, Action<NoticeAction> act) {
        var existing = Open.FirstOrDefault(t => t._noticeId == notice.Id);
        if (existing is { IsDisposed: false }) {
            existing.Pulse();
            return;
        }
        string detail = item?.Due is { } due ? TaskTime.Describe(notice.Occurrence, item.HasTime) : "";
        if (item?.Recurrence != null) {
            detail += (detail.Length > 0 ? " · " : "") + "🔁 " + RecurrenceMath.Describe(item.Recurrence);
        }
        if (projects.Count > 0) {
            detail += (detail.Length > 0 ? " · " : "") + "📁 " + string.Join(", ", projects);
        }
        var toast = new ReminderToast(notice.Id, notice.Title, detail, notice.Late, act);
        Open.Add(toast);
        Restack();
        toast.Show();
    }

    public static void Close(string noticeId) {
        foreach (var toast in Open.Where(t => t._noticeId == noticeId).ToList()) {
            toast.Dismiss();
        }
    }

    public static void CloseAll() {
        foreach (var toast in Open.ToList()) {
            toast.Dismiss();
        }
    }

    private ReminderToast(string noticeId, string title, string detail, bool late, Action<NoticeAction> act) {
        _noticeId = noticeId;
        _title = title;
        _detail = detail;
        _late = late;
        _act = act;
        _titleFont = HudTheme.ContentFont(12f, title, FontStyle.Bold);
        _detailFont = HudTheme.ContentFont(9.5f, detail);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = HudTheme.Surface;
        Font = HudTheme.BodyText;
        DoubleBuffered = true;
        Size = new Size(WidthDip, 150);

        _done = Button(Loc.T("Done"), HudButton.Kind.Primary, (_, _) => Act(NoticeAction.Done));
        _snooze = Button(Loc.T("Snooze ▾"), HudButton.Kind.Ghost, (_, _) => ShowMenu(_snoozeMenu!, _snooze!));
        _stop = Button(Loc.T("Stop reminder"), HudButton.Kind.Ghost, (_, _) => Act(NoticeAction.Stop));
        _more = Button("⋯", HudButton.Kind.Ghost, (_, _) => ShowMenu(_moreMenu!, _more!));

        _snoozeMenu = new ContextMenuStrip();
        _snoozeMenu.Items.Add(Loc.T("10 minutes"), null, (_, _) => Act(NoticeAction.Snooze10));
        _snoozeMenu.Items.Add(Loc.T("1 hour"), null, (_, _) => Act(NoticeAction.Snooze60));
        _snoozeMenu.Items.Add(Loc.T("Tomorrow"), null, (_, _) => Act(NoticeAction.SnoozeTomorrow));
        Loc.ApplyTo(_snoozeMenu);
        _moreMenu = new ContextMenuStrip();
        _moreMenu.Items.Add(Loc.T("Delete task"), null, (_, _) => Act(NoticeAction.Delete));
        Loc.ApplyTo(_moreMenu);

        Controls.Add(_done);
        Controls.Add(_snooze);
        Controls.Add(_stop);
        Controls.Add(_more);
    }

    private HudButton Button(string text, HudButton.Kind kind, EventHandler click) {
        var button = new HudButton {
            Text = text,
            StyleKind = kind,
            Font = HudTheme.SmallBold,
            BackColor = HudTheme.Surface,
        };
        button.Click += click;
        return button;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
            cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        LayoutCard();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e);
        LayoutCard();
        Restack();
    }

    private void LayoutCard() {
        int w = HudTheme.Dip(this, WidthDip);
        int pad = HudTheme.Dip(this, 18);
        int buttonH = HudTheme.Dip(this, 34);
        int titleH = TitleHeight(w - pad * 2);
        int h = pad + HudTheme.Dip(this, 20) + titleH + (_detail.Length > 0 ? HudTheme.Dip(this, 22) : 0)
            + HudTheme.Dip(this, 14) + buttonH + pad;
        Size = new Size(w, h);
        int y = h - pad - buttonH;
        int gap = HudTheme.Dip(this, 8);
        int moreW = HudTheme.Dip(this, 40);
        int each = (w - pad * 2 - moreW - gap * 3) / 3;
        var rects = new[] {
            new Rectangle(pad, y, each, buttonH),
            new Rectangle(pad + each + gap, y, each, buttonH),
            new Rectangle(pad + (each + gap) * 2, y, each, buttonH),
            new Rectangle(w - pad - moreW, y, moreW, buttonH),
        };
        var buttons = new[] { _done, _snooze, _stop, _more };
        for (int i = 0; i < buttons.Length; i++) {
            buttons[i].Bounds = Loc.Mirror(rects[i], w);
        }
        using var path = HudTheme.Rounded(new Rectangle(0, 0, w, h), HudTheme.Dip(this, 16));
        Region = new Region(path);
    }

    private int TitleHeight(int width) {
        var size = TextRenderer.MeasureText(_title, TitleFont, new Size(width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int line = TextRenderer.MeasureText("Ag", TitleFont).Height;
        return Math.Clamp(size.Height, line, line * 3);
    }

    private Font TitleFont => _titleFont;

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Surface);
        int w = ClientSize.Width;
        int pad = HudTheme.Dip(this, 18);
        using (var pen = new Pen(HudTheme.HairlineStrong, 1f))
        using (var path = HudTheme.Rounded(new Rectangle(0, 0, w - 1, ClientSize.Height - 1), HudTheme.Dip(this, 16))) {
            e.Graphics.DrawPath(pen, path);
        }
        // Accent bar on the leading edge: amber for late reminders, ink otherwise.
        using (var accent = new SolidBrush(_late ? HudTheme.Caution : HudTheme.Info)) {
            var bar = Loc.Mirror(new Rectangle(0, HudTheme.Dip(this, 16), HudTheme.Dip(this, 4), ClientSize.Height - HudTheme.Dip(this, 32)), w);
            e.Graphics.FillRectangle(accent, bar);
        }
        const TextFormatFlags single = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        string eyebrow = "⏰ " + Loc.T("Reminder") + (_late ? " · " + Loc.T("late") : "");
        TextRenderer.DrawText(e.Graphics, eyebrow, HudTheme.SmallBold,
            Loc.Mirror(new Rectangle(pad, pad, w - pad * 2, HudTheme.Dip(this, 18)), w),
            _late ? HudTheme.Caution : HudTheme.InkMuted, Loc.Flags(single));
        int titleTop = pad + HudTheme.Dip(this, 20);
        int titleH = TitleHeight(w - pad * 2);
        TextRenderer.DrawText(e.Graphics, _title, TitleFont,
            Loc.Mirror(new Rectangle(pad, titleTop, w - pad * 2, titleH), w), HudTheme.Ink,
            Loc.Flags(TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding));
        if (_detail.Length > 0) {
            TextRenderer.DrawText(e.Graphics, _detail, _detailFont,
                Loc.Mirror(new Rectangle(pad, titleTop + titleH + HudTheme.Dip(this, 2), w - pad * 2, HudTheme.Dip(this, 20)), w),
                HudTheme.InkSoft, Loc.Flags(single));
        }
    }

    private void ShowMenu(ContextMenuStrip menu, Control anchor) {
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void Act(NoticeAction action) {
        if (_acted) {
            return;
        }
        _acted = true;
        try {
            _act(action);
        } catch (Exception ex) {
            Log.Write("reminder toast: action failed: " + ex.Message);
        }
        Dismiss();
    }

    /// <summary>Brings an already open card back to the front.</summary>
    private void Pulse() {
        if (IsDisposed) {
            return;
        }
        if (!Visible) {
            Show();
        }
        NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void Dismiss() {
        Open.Remove(this);
        if (!IsDisposed) {
            Hide();
            _snoozeMenu.Dispose();
            _moreMenu.Dispose();
            Dispose();
        }
        Restack();
    }

    /// <summary>Newest at the bottom, older ones above, along the right edge of the work area.</summary>
    private static void Restack() {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int margin = 16;
        // Leave room for the dictation HUD pill along the bottom centre.
        int y = area.Bottom - margin;
        for (int i = Open.Count - 1; i >= 0; i--) {
            var toast = Open[i];
            if (toast.IsDisposed) {
                continue;
            }
            if (!toast.IsHandleCreated) {
                toast.CreateControl();
                toast.LayoutCard();
            }
            y -= toast.Height;
            int x = Loc.Rtl ? area.Left + margin : area.Right - toast.Width - margin;
            toast.Location = new Point(x, Math.Max(area.Top + margin, y));
            y -= 10;
        }
    }
}
