using EchoType.Native;

namespace EchoType.Ui;

/// <summary>One entry in the action palette.</summary>
internal sealed record PaletteAction(string Title, string? Hint, string? Shortcut, Action Run, string Group = "");

/// <summary>
/// The action palette: one shortcut opens it next to the cursor and every EchoType action is
/// a click, a number key (1–9) or a few typed letters away — so you only need to remember one
/// key. Esc (or clicking elsewhere) closes it without doing anything.
/// </summary>
internal sealed class ActionPaletteForm : Form {

    private static ActionPaletteForm? _current;

    private readonly IReadOnlyList<PaletteAction> _all;
    private readonly TextBox _filter;
    private readonly ListBox _list;
    private readonly Label _hint;
    private List<PaletteAction> _shown = [];
    private PaletteAction? _chosen;
    private Font? _boldFont;

    public static bool IsOpen => _current is { IsDisposed: false };

    public static void CloseCurrent() {
        if (_current is { IsDisposed: false } form) {
            form.Close();
        }
    }

    /// <summary>Shows the palette; <paramref name="chosen"/> runs once the palette has closed.</summary>
    public static void ShowPalette(IReadOnlyList<PaletteAction> actions, Action<PaletteAction> chosen) {
        CloseCurrent();
        var form = new ActionPaletteForm(actions);
        _current = form;
        form.FormClosed += (_, _) => {
            if (ReferenceEquals(_current, form)) {
                _current = null;
            }
            if (form._chosen is { } action) {
                chosen(action);
            }
        };
        form.PlaceNearCursor();
        form.Show();
        form.BringToForeground();
    }

    private ActionPaletteForm(IReadOnlyList<PaletteAction> actions) {
        _all = actions;
        Text = "EchoType";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        Padding = new Padding(12);
        ClientSize = new Size(440, 470);

        _filter = new TextBox {
            Dock = DockStyle.Top,
            PlaceholderText = Loc.T("Type to filter, or press 1–9"),
        };
        _filter.TextChanged += (_, _) => Reload();

        _hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = Loc.T("Enter runs · ↑↓ move · Esc closes"),
            Padding = new Padding(0, 8, 0, 0),
        };

        _list = new ListBox {
            Dock = DockStyle.Fill,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 44,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
        };
        _list.DrawItem += OnDrawItem;
        _list.MouseClick += (_, e) => {
            int i = _list.IndexFromPoint(e.Location);
            if (i >= 0) {
                Choose(i);
            }
        };

        var gap = new Panel { Dock = DockStyle.Top, Height = 8 };
        Controls.Add(_list);
        Controls.Add(gap);
        Controls.Add(_filter);
        Controls.Add(_hint);

        Deactivate += (_, _) => {
            if (!IsDisposed && _chosen == null) {
                Close();
            }
        };
        // Mirrored by hand (the rows are owner-drawn with Loc.Mirror), so no RightToLeftLayout.
        if (Loc.Rtl) {
            _filter.RightToLeft = RightToLeft.Yes;
            _hint.RightToLeft = RightToLeft.Yes;
        }
        DialogTheme.Apply(this);
        _list.BackColor = HudTheme.Surface;
        Reload();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _list.ItemHeight = HudTheme.Dip(this, 44);
        _filter.Select();
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void Reload() {
        string q = _filter.Text.Trim();
        _shown = q.Length == 0
            ? _all.ToList()
            : _all.Where(a => Matches(a, q)).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var a in _shown) {
            _list.Items.Add(a.Title);
        }
        _list.EndUpdate();
        if (_shown.Count > 0) {
            _list.SelectedIndex = 0;
        }
    }

    /// <summary>Every typed word must appear in the title, group or hint.</summary>
    private static bool Matches(PaletteAction a, string query) {
        string hay = a.Title + " " + a.Group + " " + a.Hint;
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => hay.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        switch (keyData) {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Enter:
                if (_list.SelectedIndex >= 0) {
                    Choose(_list.SelectedIndex);
                }
                return true;
            case Keys.Down:
                if (_shown.Count > 0) {
                    _list.SelectedIndex = (_list.SelectedIndex + 1) % _shown.Count;
                }
                return true;
            case Keys.Up:
                if (_shown.Count > 0) {
                    _list.SelectedIndex = (_list.SelectedIndex - 1 + _shown.Count) % _shown.Count;
                }
                return true;
        }
        // Number keys pick directly while nothing has been typed.
        if (_filter.TextLength == 0) {
            int digit = keyData switch {
                >= Keys.D1 and <= Keys.D9 => keyData - Keys.D0,
                >= Keys.NumPad1 and <= Keys.NumPad9 => keyData - Keys.NumPad0,
                _ => 0,
            };
            if (digit > 0 && digit <= _shown.Count) {
                Choose(digit - 1);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Choose(int index) {
        if (index < 0 || index >= _shown.Count) {
            return;
        }
        _chosen = _shown[index];
        Close();
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e) {
        if (e.Index < 0 || e.Index >= _shown.Count) {
            return;
        }
        var a = _shown[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var fill = new SolidBrush(selected ? HudTheme.Well : HudTheme.Surface)) {
            e.Graphics.FillRectangle(fill, e.Bounds);
        }
        int pad = HudTheme.Dip(this, 10);
        var r = Rectangle.Inflate(e.Bounds, -pad, 0);
        _boldFont ??= new Font(_list.Font, FontStyle.Bold);

        // Number badge for the first nine rows while nothing is typed.
        int badgeW = HudTheme.Dip(this, 22);
        if (_filter.TextLength == 0 && e.Index < 9) {
            var badge = Loc.Mirror(new Rectangle(r.Left, r.Top + (r.Height - badgeW) / 2, badgeW, badgeW), e.Bounds.Width + e.Bounds.Left * 2);
            using (var path = HudTheme.Rounded(badge, HudTheme.Dip(this, 6)))
            using (var b = new SolidBrush(selected ? HudTheme.Ink : HudTheme.Well)) {
                e.Graphics.FillPath(b, path);
            }
            TextRenderer.DrawText(e.Graphics, (e.Index + 1).ToString(), HudTheme.SmallBold, badge,
                selected ? HudTheme.OnInk : HudTheme.InkSoft,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        int textLeft = r.Left + badgeW + pad;
        int shortcutW = string.IsNullOrEmpty(a.Shortcut) ? 0 : HudTheme.Dip(this, 120);
        var text = new Rectangle(textLeft, r.Top, Math.Max(10, r.Right - textLeft - shortcutW), r.Height);
        HudTheme.DrawRowText(e.Graphics, Loc.Mirror(text, e.Bounds.Width + e.Bounds.Left * 2), a.Title, a.Hint);
        if (shortcutW > 0) {
            var sc = Loc.Mirror(new Rectangle(r.Right - shortcutW, r.Top, shortcutW, r.Height), e.Bounds.Width + e.Bounds.Left * 2);
            TextRenderer.DrawText(e.Graphics, a.Shortcut, HudTheme.Tiny, sc, HudTheme.InkMuted,
                Loc.Flags(TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix));
        }
    }

    private void PlaceNearCursor() {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = Cursor.Position.X + 12;
        int y = Cursor.Position.Y + 12;
        if (x + Width > area.Right) x = area.Right - Width - 8;
        if (y + Height > area.Bottom) y = area.Bottom - Height - 8;
        Location = new Point(Math.Max(area.Left + 8, x), Math.Max(area.Top + 8, y));
    }

    /// <summary>Opened from a global shortcut while another app has the keyboard: take focus anyway.</summary>
    private void BringToForeground() {
        uint ourThread = NativeMethods.GetCurrentThreadId();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        bool attached = fgThread != 0 && fgThread != ourThread
            && NativeMethods.AttachThreadInput(ourThread, fgThread, true);
        try {
            Activate();
            NativeMethods.SetForegroundWindow(Handle);
        } finally {
            if (attached) {
                NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
        }
    }
}
