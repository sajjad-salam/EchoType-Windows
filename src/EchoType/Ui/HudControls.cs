using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace EchoType.Ui;

internal sealed class HudCard : Panel {

    private Color? _accent;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? Accent {
        get => _accent;
        set {
            if (_accent == value) {
                return;
            }
            _accent = value;
            Invalidate();
        }
    }

    public HudCard() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        Padding = new Padding(28, 26, 28, 24);
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Window);
        int shadow = HudTheme.Dip(this, 7);
        int radius = HudTheme.Dip(this, HudTheme.PillRadiusDip);
        var pill = Rectangle.Inflate(ClientRectangle, -shadow, -shadow);
        if (pill.Width >= 8 && pill.Height >= 8) {
            HudTheme.DrawShadow(e.Graphics, pill, radius);
            HudTheme.DrawCard(e.Graphics, pill, radius);
            if (_accent is { } accent) {
                HudTheme.DrawAccentStripe(e.Graphics, pill, accent, HudTheme.Dip(this, 18));
            }
        }
        base.OnPaint(e);
    }
}

internal sealed class HudButton : Control {

    public enum Kind {
        Ghost,
        Primary,
        Danger,
    }

    private readonly System.Windows.Forms.Timer _tick;
    private Kind _kind = Kind.Ghost;
    private float _hover;
    private float _hoverTo;
    private bool _pressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Kind StyleKind {
        get => _kind;
        set {
            _kind = value;
            Invalidate();
        }
    }

    public HudButton() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 38;
        _tick = new System.Windows.Forms.Timer { Interval = 16 };
        _tick.Tick += (_, _) => {
            float next = HudTheme.Lerp(_hover, _hoverTo, 0.28f);
            if (Math.Abs(next - _hover) < 0.01f) {
                next = _hoverTo;
                if (!_pressed) {
                    _tick.Stop();
                }
            }
            _hover = next;
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) {
        base.OnMouseEnter(e);
        _hoverTo = 1;
        _tick.Start();
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        _pressed = false;
        _hoverTo = 0;
        _tick.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) {
            _pressed = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e) {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e) {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode is Keys.Enter or Keys.Space) {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Card);
        int radius = HudTheme.Dip(this, HudTheme.ControlRadiusDip);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill = _kind switch {
            Kind.Primary => Color.FromArgb(36 + (int)(20 * _hover), HudTheme.Teal),
            Kind.Danger => Color.FromArgb(28 + (int)(18 * _hover), HudTheme.Red),
            _ => Color.FromArgb((int)(18 + 22 * _hover), 255, 255, 255),
        };
        if (_pressed) {
            fill = Color.FromArgb(Math.Min(255, fill.A + 18), fill);
        }
        Color border = _kind switch {
            Kind.Primary => Color.FromArgb(90 + (int)(40 * _hover), HudTheme.Teal),
            Kind.Danger => Color.FromArgb(80 + (int)(40 * _hover), HudTheme.Red),
            _ => HudTheme.Border,
        };
        using (var path = HudTheme.Rounded(rect, radius))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, 1f)) {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }
        if (Focused) {
            var ring = Rectangle.Inflate(rect, -2, -2);
            using var path = HudTheme.Rounded(ring, Math.Max(4, radius - 2));
            using var pen = new Pen(Color.FromArgb(140, HudTheme.Teal), 1.5f);
            e.Graphics.DrawPath(pen, path);
        }
        Color text = _kind switch {
            Kind.Primary => HudTheme.Teal,
            Kind.Danger => HudTheme.Red,
            _ => HudTheme.Body,
        };
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal sealed class HudToggleRow : Control {

    private readonly System.Windows.Forms.Timer _tick;
    private bool _checked;
    private float _knob;
    private bool _pressed;

    public event EventHandler? CheckedChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked {
        get => _checked;
        set => SetChecked(value, notify: true);
    }

    public void SetSilent(bool value) => SetChecked(value, notify: false);

    public HudToggleRow() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 44;
        _tick = new System.Windows.Forms.Timer { Interval = 16 };
        _tick.Tick += (_, _) => {
            float target = _checked ? 1 : 0;
            float next = HudTheme.Lerp(_knob, target, 0.28f);
            if (Math.Abs(next - target) < 0.012f) {
                next = target;
                _tick.Stop();
            }
            _knob = next;
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SetChecked(bool value, bool notify) {
        if (_checked == value) {
            _knob = value ? 1 : 0;
            Invalidate();
            return;
        }
        _checked = value;
        _tick.Start();
        if (notify) {
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) {
            _pressed = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        if (_pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) {
            Checked = !_checked;
        }
        _pressed = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode is Keys.Enter or Keys.Space) {
            Checked = !_checked;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e) {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Card);
        int trackW = HudTheme.Dip(this, 44);
        int trackH = HudTheme.Dip(this, 24);
        int gap = HudTheme.Dip(this, 12);
        var track = new Rectangle(Width - trackW, (Height - trackH) / 2, trackW, trackH);
        var label = new Rectangle(0, 0, Math.Max(0, track.Left - gap), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, label, HudTheme.Body,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        Color off = HudTheme.Track;
        Color on = Color.FromArgb(255, 28, 78, 72);
        using (var path = HudTheme.Rounded(track, trackH / 2))
        using (var brush = new SolidBrush(HudTheme.Lerp(off, on, _knob))) {
            e.Graphics.FillPath(brush, path);
        }
        float pad = HudTheme.DipF(this, 3);
        float knob = trackH - pad * 2;
        float x = HudTheme.Lerp(track.Left + pad, track.Right - pad - knob, HudTheme.EaseOutCubic(_knob));
        using (var knobBrush = new SolidBrush(HudTheme.Body)) {
            e.Graphics.FillEllipse(knobBrush, x, track.Top + pad, knob, knob);
        }
        if (Focused) {
            using var path = HudTheme.Rounded(Rectangle.Inflate(track, 3, 3), trackH / 2 + 3);
            using var pen = new Pen(Color.FromArgb(140, HudTheme.Teal), 1.5f);
            e.Graphics.DrawPath(pen, path);
        }
    }
}

internal sealed class HudSegmented : Control {

    private readonly System.Windows.Forms.Timer _tick;
    private string[] _items = [];
    private int _index;
    private float _slide;
    private int _hover = -1;

    public event EventHandler? SelectedIndexChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string[] Items {
        get => _items;
        set {
            _items = value ?? [];
            if (_index >= _items.Length) {
                _index = Math.Max(0, _items.Length - 1);
            }
            _slide = _index;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex {
        get => _index;
        set => SetIndex(value, notify: true);
    }

    public void SetSilent(int value) => SetIndex(value, notify: false);

    public HudSegmented() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 40;
        _tick = new System.Windows.Forms.Timer { Interval = 16 };
        _tick.Tick += (_, _) => {
            float next = HudTheme.Lerp(_slide, _index, 0.22f);
            if (Math.Abs(next - _index) < 0.012f) {
                next = _index;
                _tick.Stop();
            }
            _slide = next;
            Invalidate();
        };
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }

    private void SetIndex(int value, bool notify) {
        if (_items.Length == 0) {
            return;
        }
        value = Math.Clamp(value, 0, _items.Length - 1);
        if (_index == value) {
            _slide = value;
            Invalidate();
            return;
        }
        _index = value;
        _tick.Start();
        if (notify) {
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        base.OnMouseMove(e);
        int next = Hit(e.X);
        if (next != _hover) {
            _hover = next;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) {
            Focus();
            int hit = Hit(e.X);
            if (hit >= 0) {
                SelectedIndex = hit;
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Left) {
            SelectedIndex = _index - 1;
            e.Handled = true;
        } else if (e.KeyCode == Keys.Right) {
            SelectedIndex = _index + 1;
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private int Hit(int x) {
        if (_items.Length == 0 || Width <= 0) {
            return -1;
        }
        int pad = HudTheme.Dip(this, 4);
        float slot = (Width - pad * 2) / (float)_items.Length;
        int i = (int)((x - pad) / slot);
        return i < 0 || i >= _items.Length ? -1 : i;
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Card);
        if (_items.Length == 0) {
            return;
        }
        int radius = HudTheme.Dip(this, 12);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = HudTheme.Rounded(bounds, radius))
        using (var fill = new SolidBrush(HudTheme.Track))
        using (var border = new Pen(HudTheme.Border, 1f)) {
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        int pad = HudTheme.Dip(this, 4);
        float slot = (Width - pad * 2) / (float)_items.Length;
        float thumbX = pad + _slide * slot;
        var thumb = new RectangleF(thumbX, pad, slot, Height - pad * 2 - 1);
        using (var path = HudTheme.Rounded(thumb, radius - 2))
        using (var fill = new SolidBrush(Color.FromArgb(255, 28, 31, 36)))
        using (var border = new Pen(Color.FromArgb(70, HudTheme.Teal), 1f)) {
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        for (int i = 0; i < _items.Length; i++) {
            var cell = new Rectangle((int)Math.Round(pad + i * slot), 0, (int)Math.Round(slot), Height);
            Color color = i == _index ? HudTheme.Teal : i == _hover ? HudTheme.Body : HudTheme.Muted;
            TextRenderer.DrawText(e.Graphics, _items[i], HudTheme.SmallBold, cell, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        if (Focused) {
            using var path = HudTheme.Rounded(Rectangle.Inflate(bounds, -2, -2), Math.Max(4, radius - 2));
            using var pen = new Pen(Color.FromArgb(120, HudTheme.Teal), 1.5f);
            e.Graphics.DrawPath(pen, path);
        }
    }
}

internal sealed class HudHotkeyRow : Control {

    private bool _capturing;
    private bool _hover;

    public event EventHandler? CaptureRequested;
    public event EventHandler? ClearRequested;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Optional { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Capturing {
        get => _capturing;
        set {
            _capturing = value;
            Invalidate();
        }
    }

    public HudHotkeyRow() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable
            | ControlStyles.UserPaint, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 56;
    }

    protected override void OnMouseEnter(EventArgs e) {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        _hover = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) {
            return;
        }
        Focus();
        if (Optional && ClearBounds().Contains(e.Location)) {
            ClearRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        CaptureRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode is Keys.Enter or Keys.Space) {
            CaptureRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        } else if (Optional && e.KeyCode == Keys.Delete) {
            ClearRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e) {
        base.OnLostFocus(e);
        Invalidate();
    }

    private Rectangle ChipBounds() {
        int w = Math.Min(HudTheme.Dip(this, 168), Width / 2);
        int h = HudTheme.Dip(this, 32);
        return new Rectangle(Width - w, (Height - h) / 2, w, h);
    }

    private Rectangle ClearBounds() {
        var chip = ChipBounds();
        int size = HudTheme.Dip(this, 18);
        return new Rectangle(chip.Right - size - HudTheme.Dip(this, 8), chip.Top + (chip.Height - size) / 2, size, size);
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Card);
        var chip = ChipBounds();
        var label = new Rectangle(0, 0, Math.Max(0, chip.Left - HudTheme.Dip(this, 12)), Height);
        TextRenderer.DrawText(e.Graphics, Title, HudTheme.BodyBold,
            new Rectangle(label.X, label.Y + HudTheme.Dip(this, 8), label.Width, HudTheme.Dip(this, 20)),
            HudTheme.Title, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(e.Graphics, _capturing ? "Press a key…" : "Click to change", HudTheme.Tiny,
            new Rectangle(label.X, label.Y + HudTheme.Dip(this, 28), label.Width, HudTheme.Dip(this, 18)),
            HudTheme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        Color fill = _capturing
            ? Color.FromArgb(40, HudTheme.Teal)
            : Color.FromArgb(_hover || Focused ? 32 : 22, 255, 255, 255);
        Color border = _capturing ? Color.FromArgb(160, HudTheme.Teal) : HudTheme.Border;
        using (var path = HudTheme.Rounded(chip, HudTheme.Dip(this, 10)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, 1f)) {
            e.Graphics.FillPath(brush, path);
            e.Graphics.DrawPath(pen, path);
        }

        string text = _capturing ? "Listening" : (string.IsNullOrWhiteSpace(Value) ? "(none)" : Value);
        var textRect = chip;
        if (Optional && !_capturing) {
            textRect.Width -= HudTheme.Dip(this, 22);
        }
        TextRenderer.DrawText(e.Graphics, text, Font, textRect,
            _capturing ? HudTheme.Teal : HudTheme.Body,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (Optional && !_capturing) {
            var clear = ClearBounds();
            using var pen = new Pen(HudTheme.Muted, 1.4f);
            e.Graphics.DrawLine(pen, clear.Left + 4, clear.Top + 4, clear.Right - 4, clear.Bottom - 4);
            e.Graphics.DrawLine(pen, clear.Right - 4, clear.Top + 4, clear.Left + 4, clear.Bottom - 4);
        }
    }
}

internal sealed class HudCaptionButton : Control {

    public enum Glyph {
        Minimize,
        Close,
    }

    private bool _hover;
    private bool _pressed;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Glyph Icon { get; set; }

    public HudCaptionButton() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Size = new Size(36, 28);
    }

    protected override void OnMouseEnter(EventArgs e) {
        base.OnMouseEnter(e);
        _hover = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Window);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_hover || _pressed) {
            Color fill = Icon == Glyph.Close
                ? Color.FromArgb(_pressed ? 70 : 48, 248, 113, 113)
                : Color.FromArgb(_pressed ? 40 : 28, 255, 255, 255);
            using var path = HudTheme.Rounded(rect, HudTheme.Dip(this, 8));
            using var brush = new SolidBrush(fill);
            e.Graphics.FillPath(brush, path);
        }
        using var pen = new Pen(Icon == Glyph.Close && _hover ? HudTheme.Red : HudTheme.Title, 1.4f) {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        int cx = Width / 2;
        int cy = Height / 2;
        int s = HudTheme.Dip(this, 5);
        if (Icon == Glyph.Minimize) {
            e.Graphics.DrawLine(pen, cx - s, cy, cx + s, cy);
        } else {
            e.Graphics.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
            e.Graphics.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
        }
    }
}

internal sealed class HudCommandList : Control {

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<(string Name, string Shortcut)> Items { get; set; } = [];

    public HudCommandList() {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
        Height = 48;
    }

    public void Reload(IReadOnlyList<(string Name, string Shortcut)> items) {
        Items = items;
        int row = HudTheme.Dip(this, 28);
        Height = items.Count == 0 ? HudTheme.Dip(this, 28) : Math.Min(items.Count, 6) * row + HudTheme.Dip(this, 4);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Card);
        if (Items.Count == 0) {
            TextRenderer.DrawText(e.Graphics, "No custom commands yet.", HudTheme.Small, ClientRectangle,
                HudTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            return;
        }
        int row = HudTheme.Dip(this, 28);
        int shown = Math.Min(Items.Count, 6);
        for (int i = 0; i < shown; i++) {
            var (name, shortcut) = Items[i];
            if (i == 5 && Items.Count > 6) {
                name = "+" + (Items.Count - 5) + " more";
                shortcut = "";
            }
            var y = i * row;
            TextRenderer.DrawText(e.Graphics, name, HudTheme.BodyBold,
                new Rectangle(0, y, Width / 2, row), HudTheme.Body,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, shortcut, HudTheme.Small,
                new Rectangle(Width / 2, y, Width / 2, row), HudTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }
}
