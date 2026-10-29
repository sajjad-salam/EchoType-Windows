using EchoType.Native;
using EchoType.Tasks;

namespace EchoType.Ui;

/// <summary>
/// The quick-add bar: a search-bar-like box in the middle of the screen, opened by a global
/// shortcut from any app. Type and press Enter → the idea lands in the Inbox (and in every
/// project it names). Tab switches idea/task, Ctrl+Enter hands the text to the assistant
/// ("call Ali tomorrow at 5", "delete the last one"), the mic talks to it instead. Esc closes.
/// </summary>
internal sealed class QuickAddForm : Form {

    private const int WidthDip = 660;

    private static QuickAddForm? _current;

    private readonly TextBox _box;
    private readonly HudButton _kindButton;
    private readonly HudButton _mic;
    private readonly IReadOnlyList<Project> _projects;
    private readonly Action<string, ItemKind> _add;
    private readonly Action<string> _ask;
    private readonly Action _voice;
    private readonly Font _latinFont;
    private readonly Font _arabicFont;
    private readonly Font _hintLatin;
    private readonly Font _hintArabic;
    private ItemKind _kind = ItemKind.Idea;
    private string _hint;
    private readonly string _micHint;
    private bool _closing;

    /// <summary>Opens the bar, or brings the open one back.</summary>
    public static void Open(IReadOnlyList<Project> projects, Action<string, ItemKind> add, Action<string> ask, Action voice,
        string micHint) {
        if (_current is { IsDisposed: false } open) {
            open.BringToForeground();
            return;
        }
        _current = new QuickAddForm(projects, add, ask, voice, micHint);
        _current.Show();
        _current.BringToForeground();
    }

    public static void CloseCurrent() => _current?.CloseBar();

    private QuickAddForm(IReadOnlyList<Project> projects, Action<string, ItemKind> add, Action<string> ask, Action voice,
        string micHint) {
        _micHint = micHint;
        _projects = projects;
        _add = add;
        _ask = ask;
        _voice = voice;
        _hint = DefaultHint();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = HudTheme.Surface;
        DoubleBuffered = true;
        Text = Loc.T("Quick add");

        _latinFont = HudTheme.Font(15f);
        _arabicFont = ArabicFont.Create(15f) ?? _latinFont;
        _hintLatin = HudTheme.Font(9f);
        _hintArabic = ArabicFont.Create(9f) ?? _hintLatin;
        _box = new TextBox {
            BorderStyle = BorderStyle.None,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = Loc.Arabic ? _arabicFont : _latinFont,
            PlaceholderText = Loc.T("Add an idea…"),
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        _box.TextChanged += (_, _) => OnTextChanged();
        _kindButton = new HudButton { StyleKind = HudButton.Kind.Ghost, Font = HudTheme.SmallBold, BackColor = HudTheme.Surface, TabStop = false };
        _kindButton.Click += (_, _) => ToggleKind();
        _mic = new HudButton { Text = "🎙", StyleKind = HudButton.Kind.Primary, Font = HudTheme.BodyText, BackColor = HudTheme.Surface, TabStop = false };
        _mic.Click += (_, _) => {
            CloseBar();
            _voice();
        };
        Controls.Add(_box);
        Controls.Add(_kindButton);
        Controls.Add(_mic);
        SyncKind();
    }

    private string DefaultHint() =>
        Loc.T("Enter: add · Tab: idea/task · Ctrl+Enter: assistant") + " · " + _micHint;

    protected override bool ProcessDialogKey(Keys keyData) {
        if (keyData == Keys.Tab) {
            ToggleKind();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST);
            cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnLoad(EventArgs e) {
        base.OnLoad(e);
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = HudTheme.Dip(this, WidthDip);
        int h = HudTheme.Dip(this, 104);
        Bounds = new Rectangle(screen.Left + (screen.Width - w) / 2, screen.Top + screen.Height * 26 / 100, w, h);
        LayoutBar();
    }

    private void LayoutBar() {
        int w = ClientSize.Width;
        int pad = HudTheme.Dip(this, 20);
        int top = HudTheme.Dip(this, 18);
        int barH = HudTheme.Dip(this, 40);
        int micW = HudTheme.Dip(this, 44);
        int kindW = HudTheme.Dip(this, 96);
        int gap = HudTheme.Dip(this, 8);
        int glyph = HudTheme.Dip(this, 30);
        var mic = new Rectangle(w - pad - micW, top, micW, barH);
        var kind = new Rectangle(mic.Left - gap - kindW, top + HudTheme.Dip(this, 4), kindW, barH - HudTheme.Dip(this, 8));
        int boxLeft = pad + glyph;
        int boxH = _box.PreferredHeight;
        var box = new Rectangle(boxLeft, top + (barH - boxH) / 2, Math.Max(40, kind.Left - gap - boxLeft), boxH);
        _mic.Bounds = Loc.Mirror(mic, w);
        _kindButton.Bounds = Loc.Mirror(kind, w);
        _box.Bounds = Loc.Mirror(box, w);
        using var path = HudTheme.Rounded(new Rectangle(0, 0, w, ClientSize.Height), HudTheme.Dip(this, 18));
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Surface);
        int w = ClientSize.Width;
        int pad = HudTheme.Dip(this, 20);
        using (var pen = new Pen(HudTheme.HairlineStrong, 1f))
        using (var border = HudTheme.Rounded(new Rectangle(0, 0, w - 1, ClientSize.Height - 1), HudTheme.Dip(this, 18))) {
            e.Graphics.DrawPath(pen, border);
        }
        int top = HudTheme.Dip(this, 18);
        int barH = HudTheme.Dip(this, 40);
        var glyphRect = Loc.Mirror(new Rectangle(pad - HudTheme.Dip(this, 4), top, HudTheme.Dip(this, 28), barH), w);
        TextRenderer.DrawText(e.Graphics, _kind == ItemKind.Task ? "📌" : "💡", HudTheme.Caption, glyphRect, HudTheme.Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int lineY = top + barH + HudTheme.Dip(this, 10);
        using (var rule = new SolidBrush(HudTheme.Rule)) {
            e.Graphics.FillRectangle(rule, pad, lineY, w - pad * 2, 1);
        }
        var hint = Loc.Mirror(new Rectangle(pad, lineY + HudTheme.Dip(this, 6), w - pad * 2, HudTheme.Dip(this, 22)), w);
        TextRenderer.DrawText(e.Graphics, _hint, ArabicFont.HasArabic(_hint) ? _hintArabic : _hintLatin, hint, HudTheme.InkMuted,
            Loc.Flags(TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter));
    }

    private void OnTextChanged() {
        string text = _box.Text;
        bool arabic = ArabicFont.HasArabic(text) || (text.Length == 0 && Loc.Arabic);
        var font = arabic ? _arabicFont : _latinFont;
        if (_box.Font != font) {
            _box.Font = font;
            _box.RightToLeft = arabic ? RightToLeft.Yes : RightToLeft.No;
            LayoutBar();
        }
        var matched = ProjectMatcher.Match(text, _projects);
        _hint = matched.Count > 0
            ? "📁 " + string.Join(", ", matched.Select(p => p.Name)) + "  ·  " + Loc.T("Enter to add")
            : text.Trim().Length > 0 ? Loc.T("Enter: add to Inbox · Ctrl+Enter: let the assistant read dates, projects and commands")
            : DefaultHint();
        Invalidate();
    }

    private void ToggleKind() {
        _kind = _kind == ItemKind.Idea ? ItemKind.Task : ItemKind.Idea;
        SyncKind();
        _box.Focus();
    }

    private void SyncKind() {
        _kindButton.Text = _kind == ItemKind.Task ? "📌 " + Loc.T("Task") : "💡 " + Loc.T("Idea");
        _box.PlaceholderText = _kind == ItemKind.Task ? Loc.T("Add a task…") : Loc.T("Add an idea…");
        _kindButton.Invalidate();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape) {
            e.Handled = e.SuppressKeyPress = true;
            CloseBar();
            return;
        }
        if (e.KeyCode == Keys.Enter) {
            e.Handled = e.SuppressKeyPress = true;
            string text = _box.Text.Trim();
            if (text.Length == 0) {
                return;
            }
            CloseBar();
            if (e.Control) {
                _ask(text);
            } else {
                _add(text, _kind);
            }
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnDeactivate(EventArgs e) {
        base.OnDeactivate(e);
        // Clicking elsewhere closes the bar, like a launcher; typed text is not lost silently
        // because nothing was submitted yet (the user can reopen and retype).
        if (!_closing && _box.Text.Trim().Length == 0) {
            CloseBar();
        }
    }

    private void CloseBar() {
        if (_closing || IsDisposed) {
            return;
        }
        _closing = true;
        if (ReferenceEquals(_current, this)) {
            _current = null;
        }
        Hide();
        BeginInvoke(Dispose);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _latinFont.Dispose();
            _hintLatin.Dispose();
            if (!ReferenceEquals(_arabicFont, _latinFont)) {
                _arabicFont.Dispose();
            }
            if (!ReferenceEquals(_hintArabic, _hintLatin)) {
                _hintArabic.Dispose();
            }
        }
        base.Dispose(disposing);
    }

    private void BringToForeground() {
        uint ourThread = NativeMethods.GetCurrentThreadId();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = fgThread != 0 && fgThread != ourThread
            && NativeMethods.AttachThreadInput(ourThread, fgThread, true);
        try {
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
            _box.Focus();
            _box.SelectionStart = _box.TextLength;
        } finally {
            if (attached) {
                NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
        }
    }
}
