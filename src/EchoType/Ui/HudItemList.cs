using System.ComponentModel;

namespace EchoType.Ui;

/// <summary>A small pill button drawn inside a <see cref="HudItemList"/> row.</summary>
internal sealed record RowAction(string Label, Action Run, bool Primary = false, bool Danger = false);

/// <summary>One row of a <see cref="HudItemList"/>.</summary>
internal sealed class ListRow {
    /// <summary>Leading emoji/glyph when the row has no check circle (💡, 📁, ⏰).</summary>
    public string Glyph { get; init; } = "";
    /// <summary>Draws a round check box on the leading side (tasks).</summary>
    public bool Check { get; init; }
    public bool Checked { get; init; }
    public Action? CheckClicked { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public Color? SubtitleColor { get; init; }
    public bool Muted { get; init; }
    public Action? Clicked { get; init; }
    public List<RowAction> Actions { get; init; } = [];
    /// <summary>Show <see cref="Actions"/> only while the mouse is over the row.</summary>
    public bool ActionsOnHover { get; init; } = true;
}

/// <summary>
/// A painted list for ideas, tasks, projects and reminders: a check circle or emoji, a title
/// with a muted detail line, and small pill actions on the trailing side. It grows to fit its
/// rows (the page scrolls, not the list).
/// </summary>
internal sealed class HudItemList : Control {

    private const int RowDip = 54;
    private const int EmptyDip = 44;

    private static Font? _arabicTitle;
    private static Font? _arabicSmall;

    private List<ListRow> _rows = [];
    private int _hover = -1;
    private int _hoverAction = -1;
    private bool _hoverCheck;
    private readonly List<(int Row, int Action, Rectangle Bounds)> _actionHits = [];

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string EmptyText { get; set; } = "";

    public HudItemList() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        Height = EmptyDip;
        BackColor = HudTheme.Surface;
    }

    public int RowCount => _rows.Count;

    public void Reload(List<ListRow> rows) {
        _rows = rows;
        _hover = -1;
        _hoverAction = -1;
        Height = HeightFor(rows.Count);
        Invalidate();
    }

    private int HeightFor(int count) =>
        count == 0 ? HudTheme.Dip(this, EmptyDip) : count * HudTheme.Dip(this, RowDip);

    protected override void OnDpiChangedAfterParent(EventArgs e) {
        base.OnDpiChangedAfterParent(e);
        Height = HeightFor(_rows.Count);
    }

    private static Font TitleFont(string text) {
        if (ArabicFont.HasArabic(text)) {
            _arabicTitle ??= ArabicFont.Create(10f, FontStyle.Bold);
            if (_arabicTitle != null) {
                return _arabicTitle;
            }
        }
        return HudTheme.BodyBold;
    }

    private static Font SmallFont(string text) {
        if (ArabicFont.HasArabic(text)) {
            _arabicSmall ??= ArabicFont.Create(9f);
            if (_arabicSmall != null) {
                return _arabicSmall;
            }
        }
        return HudTheme.Small;
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Surface);
        _actionHits.Clear();
        const TextFormatFlags single = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (_rows.Count == 0) {
            TextRenderer.DrawText(e.Graphics, EmptyText, HudTheme.Small, ClientRectangle, HudTheme.InkMuted,
                Loc.Flags(single | TextFormatFlags.VerticalCenter));
            return;
        }
        int rowH = HudTheme.Dip(this, RowDip);
        int w = Width;
        for (int i = 0; i < _rows.Count; i++) {
            var row = _rows[i];
            int y = i * rowH;
            var bounds = new Rectangle(0, y, w, rowH);
            if (!e.ClipRectangle.IntersectsWith(bounds)) {
                continue;
            }
            if (i == _hover && (row.Clicked != null || row.Actions.Count > 0)) {
                using var path = HudTheme.Rounded(new Rectangle(-HudTheme.Dip(this, 8), y + 3, w + HudTheme.Dip(this, 16), rowH - 6), HudTheme.Dip(this, 10));
                using var hover = new SolidBrush(HudTheme.Lerp(HudTheme.Surface, HudTheme.Well, 0.7f));
                e.Graphics.FillPath(hover, path);
            }
            if (i > 0) {
                using var rule = new SolidBrush(HudTheme.Rule);
                e.Graphics.FillRectangle(rule, 0, y, w, 1);
            }

            int lead = HudTheme.Dip(this, 30);
            var leadRect = Loc.Mirror(new Rectangle(0, y, lead, rowH), w);
            if (row.Check) {
                int d = HudTheme.Dip(this, 18);
                var circle = new Rectangle(leadRect.Left + (lead - d) / 2 - HudTheme.Dip(this, 4), y + (rowH - d) / 2, d, d);
                if (Loc.Rtl) {
                    circle.X += HudTheme.Dip(this, 8);
                }
                bool hot = i == _hover && _hoverCheck;
                using var pen = new Pen(row.Checked ? HudTheme.Positive : hot ? HudTheme.Ink : HudTheme.HairlineStrong, HudTheme.DipF(this, 1.6f));
                if (row.Checked) {
                    using var fill = new SolidBrush(HudTheme.Positive);
                    e.Graphics.FillEllipse(fill, circle);
                } else {
                    e.Graphics.DrawEllipse(pen, circle);
                }
                if (row.Checked || hot) {
                    using var tick = new Pen(row.Checked ? HudTheme.OnInk : HudTheme.Ink, HudTheme.DipF(this, 1.6f));
                    float cx = circle.Left + circle.Width / 2f;
                    float cy = circle.Top + circle.Height / 2f;
                    float s = circle.Width / 4.5f;
                    e.Graphics.DrawLines(tick, new[] {
                        new PointF(cx - s, cy), new PointF(cx - s / 3, cy + s * 0.8f), new PointF(cx + s * 1.1f, cy - s * 0.7f),
                    });
                }
            } else if (row.Glyph.Length > 0) {
                TextRenderer.DrawText(e.Graphics, row.Glyph, HudTheme.BodyText, leadRect, HudTheme.Ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            // Trailing actions (right to left in LTR).
            int trailing = w;
            bool showActions = row.Actions.Count > 0 && (!row.ActionsOnHover || i == _hover);
            if (showActions) {
                int pillH = HudTheme.Dip(this, 28);
                int gap = HudTheme.Dip(this, 6);
                int x = w;
                for (int a = row.Actions.Count - 1; a >= 0; a--) {
                    var action = row.Actions[a];
                    int pw = TextRenderer.MeasureText(action.Label, HudTheme.SmallBold).Width + HudTheme.Dip(this, 22);
                    x -= pw;
                    var pill = new Rectangle(x, y + (rowH - pillH) / 2, pw, pillH);
                    var drawn = Loc.Mirror(pill, w);
                    _actionHits.Add((i, a, drawn));
                    bool hot = i == _hover && _hoverAction == a;
                    Color fill = action.Primary ? (hot ? HudTheme.InkHover : HudTheme.Ink)
                        : action.Danger ? (hot ? Color.FromArgb(255, 252, 222, 222) : HudTheme.NegativeWash)
                        : (hot ? HudTheme.WellHover : HudTheme.Well);
                    using (var path = HudTheme.Rounded(drawn, pillH / 2))
                    using (var brush = new SolidBrush(fill)) {
                        e.Graphics.FillPath(brush, path);
                    }
                    TextRenderer.DrawText(e.Graphics, action.Label, HudTheme.SmallBold, drawn,
                        action.Primary ? HudTheme.OnInk : action.Danger ? HudTheme.Negative : HudTheme.Ink,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                    x -= gap;
                }
                trailing = x - HudTheme.Dip(this, 6);
            }

            int textLeft = lead + HudTheme.Dip(this, 6);
            int textW = Math.Max(0, trailing - textLeft);
            Color titleColor = row.Muted || row.Checked ? HudTheme.InkMuted : HudTheme.Ink;
            bool hasSub = row.Subtitle.Length > 0;
            int titleH = HudTheme.Dip(this, 22);
            int subH = HudTheme.Dip(this, 18);
            int top = y + (rowH - titleH - (hasSub ? subH : 0)) / 2;
            TextRenderer.DrawText(e.Graphics, row.Title, TitleFont(row.Title),
                Loc.Mirror(new Rectangle(textLeft, top, textW, titleH), w), titleColor, Loc.Flags(single | TextFormatFlags.VerticalCenter));
            if (hasSub) {
                TextRenderer.DrawText(e.Graphics, row.Subtitle, SmallFont(row.Subtitle),
                    Loc.Mirror(new Rectangle(textLeft, top + titleH, textW, subH), w),
                    row.SubtitleColor ?? HudTheme.InkMuted, Loc.Flags(single | TextFormatFlags.VerticalCenter));
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        int row = RowAt(e.Location);
        int action = -1;
        foreach (var hit in _actionHits) {
            if (hit.Row == row && hit.Bounds.Contains(e.Location)) {
                action = hit.Action;
            }
        }
        bool check = row >= 0 && _rows[row].Check && InCheck(e.Location);
        if (row != _hover || action != _hoverAction || check != _hoverCheck) {
            _hover = row;
            _hoverAction = action;
            _hoverCheck = check;
            Invalidate();
        }
        bool clickable = row >= 0 && (action >= 0 || check || _rows[row].Clicked != null);
        Cursor = clickable ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        _hover = -1;
        _hoverAction = -1;
        _hoverCheck = false;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e) {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) {
            return;
        }
        int row = RowAt(e.Location);
        if (row < 0) {
            return;
        }
        var target = _rows[row];
        foreach (var hit in _actionHits) {
            if (hit.Row == row && hit.Bounds.Contains(e.Location)) {
                Run(target.Actions[hit.Action].Run);
                return;
            }
        }
        if (target.Check && InCheck(e.Location) && target.CheckClicked != null) {
            Run(target.CheckClicked);
            return;
        }
        if (target.Clicked != null) {
            Run(target.Clicked);
        }
    }

    private static void Run(Action action) {
        try {
            action();
        } catch (Exception ex) {
            Log.Write("list: action failed: " + ex);
        }
    }

    private int RowAt(Point p) {
        if (_rows.Count == 0 || p.Y < 0) {
            return -1;
        }
        int index = p.Y / Math.Max(1, HudTheme.Dip(this, RowDip));
        return index < _rows.Count ? index : -1;
    }

    private bool InCheck(Point p) {
        int lead = HudTheme.Dip(this, 34);
        return Loc.Rtl ? p.X >= Width - lead : p.X <= lead;
    }
}

/// <summary>A text box on a soft rounded well with a trailing button; Enter submits.</summary>
internal sealed class HudInputRow : Control {

    private readonly TextBox _box;
    private readonly HudButton _button;
    private Font? _arabic;
    private readonly Font _latin = HudTheme.BodyText;

    public event Action<string>? Submitted;

    public HudInputRow(string placeholder, string buttonText) {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        BackColor = HudTheme.Surface;
        _box = new TextBox {
            BorderStyle = BorderStyle.None,
            BackColor = HudTheme.Well,
            ForeColor = HudTheme.Ink,
            Font = _latin,
            PlaceholderText = placeholder,
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        _box.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Enter) {
                e.Handled = e.SuppressKeyPress = true;
                Submit();
            }
        };
        _box.TextChanged += (_, _) => {
            bool arabic = ArabicFont.HasArabic(_box.Text);
            if (arabic) {
                _arabic ??= ArabicFont.Create(10f);
            }
            var font = arabic && _arabic != null ? _arabic : _latin;
            if (_box.Font != font) {
                _box.Font = font;
                PerformLayout();
            }
        };
        _button = new HudButton { Text = buttonText, StyleKind = HudButton.Kind.Primary, Font = HudTheme.SmallBold, BackColor = HudTheme.Surface };
        _button.Click += (_, _) => Submit();
        Controls.Add(_box);
        Controls.Add(_button);
    }

    private void Submit() {
        string text = _box.Text.Trim();
        if (text.Length == 0) {
            _box.Focus();
            return;
        }
        _box.Clear();
        Submitted?.Invoke(text);
        _box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs levent) {
        base.OnLayout(levent);
        int buttonW = HudTheme.Dip(this, 96);
        int h = HudTheme.Dip(this, 38);
        int top = (Height - h) / 2;
        var well = new Rectangle(0, top, Math.Max(40, Width - buttonW - HudTheme.Dip(this, 10)), h);
        _button.Bounds = Loc.Mirror(new Rectangle(Width - buttonW, top, buttonW, h), Width);
        int boxH = _box.PreferredHeight;
        _box.Bounds = Loc.Mirror(new Rectangle(well.Left + HudTheme.Dip(this, 14), top + (h - boxH) / 2,
            well.Width - HudTheme.Dip(this, 28), boxH), Width);
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Surface);
        int buttonW = HudTheme.Dip(this, 96);
        int h = HudTheme.Dip(this, 38);
        var well = Loc.Mirror(new Rectangle(0, (Height - h) / 2, Math.Max(40, Width - buttonW - HudTheme.Dip(this, 10)), h), Width);
        using var path = HudTheme.Rounded(well, h / 2);
        using var fill = new SolidBrush(HudTheme.Well);
        e.Graphics.FillPath(fill, path);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _arabic?.Dispose();
        }
        base.Dispose(disposing);
    }
}
