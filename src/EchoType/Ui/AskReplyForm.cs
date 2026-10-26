using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using EchoType.Native;

namespace EchoType.Ui;

/// <summary>
/// What the Ask model popup shows. <paramref name="Title"/> replaces "Ask {model}" in the
/// caption; <paramref name="KeepLayout"/> shows <paramref name="Text"/> exactly as given
/// (blank lines, indentation, spacing) in a popup wide enough for its longest line.
/// </summary>
internal sealed record AskReplyContent(
    string ModelName,
    string Question,
    string Text,
    Image? Image,
    int ImageCount,
    IReadOnlyList<string> Videos,
    string DeliveryNote,
    string? Title = null,
    bool KeepLayout = false);

/// <summary>
/// Popup with the Ask model's complete reply: the text (copyable, selectable), the
/// generated image (copy or save), and a note when the reply holds a video. Opens
/// without taking focus so the field the reply was pasted into stays active; one
/// popup at a time — a new reply replaces the old one.
/// </summary>
internal sealed partial class AskReplyForm : Form {

    private const int WidthDip = 560;
    private const int MaxLayoutWidthDip = 960;
    private static AskReplyForm? _current;

    private readonly AskReplyContent _content;
    private readonly Action? _openModelWindow;
    private readonly string _text;
    private readonly Panel _caption;
    private readonly HudCaptionButton _close;
    private readonly Label _question;
    private readonly RichTextBox? _body;
    private readonly PictureBox? _picture;
    private readonly Label? _videoNote;
    private readonly Label _status;
    private readonly FlowLayoutPanel _buttons;
    private readonly HudButton? _copyText;
    private readonly HudButton? _copyImage;
    private readonly System.Windows.Forms.Timer _statusReset;
    private readonly Font _bodyFont;
    private int _dragX;
    private int _dragY;
    private bool _dragging;

    /// <summary>
    /// Shows <paramref name="content"/>, closing any previous popup. Without
    /// <paramref name="openModelWindow"/> there is no "Open in …" button.
    /// </summary>
    public static void ShowReply(AskReplyContent content, Action? openModelWindow) {
        try {
            if (_current is { IsDisposed: false } old) {
                old.Close();
            }
            var form = new AskReplyForm(content, openModelWindow);
            _current = form;
            form.PlaceNearBottom();
            form.Show();
            NativeMethods.SetWindowPos(
                form.Handle, NativeMethods.HWND_TOPMOST,
                form.Left, form.Top, form.Width, form.Height,
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        } catch (Exception ex) {
            Log.Write("ask popup: show failed: " + ex.Message);
        }
    }

    public static void CloseCurrent() {
        if (_current is { IsDisposed: false } form) {
            form.Close();
        }
        _current = null;
    }

    private AskReplyForm(AskReplyContent content, Action? openModelWindow) {
        _content = content;
        _openModelWindow = openModelWindow;
        _text = content.KeepLayout ? content.Text : CleanText(content.Text);

        Text = CaptionText;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = HudTheme.Surface;
        ForeColor = HudTheme.Ink;
        Font = HudTheme.BodyText;
        Padding = new Padding(1);

        _bodyFont = HudTheme.ContentFont(11f, _text);
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int width = content.KeepLayout ? LayoutWidth(_text, _bodyFont, screen) : WidthDip;
        Width = width;

        _caption = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = HudTheme.Surface };
        _caption.Paint += OnCaptionPaint;
        _caption.MouseDown += (_, e) => {
            if (e.Button == MouseButtons.Left) {
                _dragging = true;
                _dragX = e.X;
                _dragY = e.Y;
            }
        };
        _caption.MouseMove += (_, e) => {
            if (_dragging) {
                Location = new Point(Left + e.X - _dragX, Top + e.Y - _dragY);
            }
        };
        _caption.MouseUp += (_, _) => _dragging = false;
        _close = new HudCaptionButton { Icon = HudCaptionButton.Glyph.Close, Size = new Size(36, 28) };
        _close.Click += (_, _) => Close();
        _caption.Controls.Add(_close);
        _caption.Resize += (_, _) => PlaceClose();

        var content_ = new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = HudTheme.Surface,
            Padding = new Padding(20, 4, 20, 16),
        };

        int inner = width - 40 - 2;
        bool questionRtl = ArabicFont.StartsRightToLeft(content.Question);
        _question = new Label {
            AutoSize = false,
            Width = inner,
            Font = HudTheme.ContentFont(9.5f, content.Question),
            ForeColor = HudTheme.InkMuted,
            BackColor = HudTheme.Surface,
            Text = Truncate(content.Question, 220),
            // With RightToLeft on, WinForms mirrors TextAlign: TopLeft already means "start at the right".
            RightToLeft = questionRtl ? RightToLeft.Yes : RightToLeft.No,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 0, 0, 8),
            UseMnemonic = false,
        };
        _question.Height = Math.Min(44, TextRenderer.MeasureText(_question.Text, _question.Font,
            new Size(inner, 200), TextFormatFlags.WordBreak).Height + 4);
        content_.Controls.Add(_question);

        if (_text.Length > 0) {
            bool rtl = ArabicFont.StartsRightToLeft(_text);
            _body = new RichTextBox {
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = HudTheme.Surface,
                ForeColor = HudTheme.Ink,
                Font = _bodyFont,
                Width = inner,
                DetectUrls = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No,
                Margin = new Padding(0, 0, 0, 10),
                Text = _text,
                ShortcutsEnabled = true,
            };
            _body.LinkClicked += (_, e) => OpenUrl(e.LinkText ?? "");
            int textHeight = TextRenderer.MeasureText(_text, _bodyFont, new Size(inner - 24, 10_000),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.ExpandTabs).Height + 12;
            int maxText = content.Image != null ? screen.Height / 4 : screen.Height / 2;
            _body.Height = Math.Clamp(textHeight, 40, Math.Max(120, maxText));
            content_.Controls.Add(_body);
        }

        if (content.Image != null) {
            float scale = Math.Min(1f, Math.Min(inner / (float)content.Image.Width, 340f / content.Image.Height));
            _picture = new PictureBox {
                Image = content.Image,
                SizeMode = PictureBoxSizeMode.Zoom,
                Width = inner,
                Height = Math.Max(80, (int)(content.Image.Height * scale)),
                BackColor = HudTheme.Well,
                Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 0, 10),
            };
            _picture.Click += (_, _) => CopyImage();
            new ToolTip().SetToolTip(_picture, Loc.T("Click to copy the image"));
            content_.Controls.Add(_picture);
        }

        if (content.Videos.Count > 0 || (content.ImageCount > 1 && content.Image != null)) {
            string note = content.Videos.Count > 0
                ? Loc.F("The reply contains a video. Open it in the {0} window to play or download it.", content.ModelName)
                : Loc.F("The reply has {0} images; the largest is shown. Open the {1} window to see all of them.",
                    content.ImageCount, content.ModelName);
            _videoNote = new Label {
                AutoSize = false,
                Width = inner,
                Font = HudTheme.Small,
                ForeColor = HudTheme.Caution,
                BackColor = HudTheme.Surface,
                Text = note,
                Margin = new Padding(0, 0, 0, 8),
                UseMnemonic = false,
            };
            _videoNote.Height = TextRenderer.MeasureText(note, _videoNote.Font, new Size(inner, 200), TextFormatFlags.WordBreak).Height + 4;
            content_.Controls.Add(_videoNote);
        }

        _status = new Label {
            AutoSize = false,
            Width = inner,
            Height = 22,
            Font = HudTheme.Small,
            ForeColor = HudTheme.Positive,
            BackColor = HudTheme.Surface,
            Text = content.DeliveryNote,
            Margin = new Padding(0, 0, 0, 6),
            UseMnemonic = false,
        };
        content_.Controls.Add(_status);

        _buttons = new FlowLayoutPanel {
            AutoSize = true,
            WrapContents = true,
            Width = inner,
            MaximumSize = new Size(inner, 0),
            BackColor = HudTheme.Surface,
            Margin = new Padding(0),
        };
        if (_text.Length > 0) {
            _copyText = AddButton(Loc.T("Copy text"), HudButton.Kind.Primary, 120, CopyText);
        }
        if (content.Image != null) {
            _copyImage = AddButton(Loc.T("Copy image"), _text.Length > 0 ? HudButton.Kind.Ghost : HudButton.Kind.Primary, 120, CopyImage);
            AddButton(Loc.T("Save image…"), HudButton.Kind.Ghost, 120, SaveImage);
        }
        foreach (string url in content.Videos.Where(IsWebUrl).Take(1)) {
            AddButton(Loc.T("Open video"), HudButton.Kind.Ghost, 120, () => OpenUrl(url));
        }
        if (_openModelWindow != null) {
            AddButton(Loc.F("Open in {0}", content.ModelName), HudButton.Kind.Ghost, 150, _openModelWindow);
        }
        content_.Controls.Add(_buttons);

        Controls.Add(content_);
        Controls.Add(_caption);

        _statusReset = new System.Windows.Forms.Timer { Interval = 2200 };
        _statusReset.Tick += (_, _) => {
            _statusReset.Stop();
            _status.ForeColor = HudTheme.Positive;
            _status.Text = _content.DeliveryNote;
            if (_copyText != null) {
                _copyText.Text = Loc.T("Copy text");
            }
            if (_copyImage != null) {
                _copyImage.Text = Loc.T("Copy image");
            }
        };

        // Arabic UI: buttons and notes start from the right. Mirrored by hand (not
        // RightToLeftLayout) because the caption is custom-painted. The reply text and
        // question keep their own direction whatever the UI language.
        if (Loc.Rtl) {
            _buttons.FlowDirection = FlowDirection.RightToLeft;
            foreach (var label in new[] { _status, _videoNote }) {
                if (label != null) {
                    label.RightToLeft = RightToLeft.Yes; // TopLeft is mirrored to the right edge
                }
            }
        }

        int contentHeight = content_.Padding.Vertical;
        foreach (Control c in content_.Controls) {
            contentHeight += c.Height + c.Margin.Vertical;
        }
        _buttons.PerformLayout();
        contentHeight += Math.Max(0, _buttons.PreferredSize.Height - _buttons.Height);
        Height = Math.Min(screen.Height - 40, _caption.Height + contentHeight + 4);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
            cp.ExStyle |= (int)NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        HudTheme.ApplyChrome(this, paper: true);
    }

    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e);
        using var pen = new Pen(HudTheme.HairlineStrong);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape) {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.C && (_body == null || _body.SelectionLength == 0)) {
            if (_text.Length > 0) {
                CopyText();
            } else {
                CopyImage();
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        if (ReferenceEquals(_current, this)) {
            _current = null;
        }
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _statusReset.Dispose();
            _bodyFont.Dispose();
            _content.Image?.Dispose();
        }
        base.Dispose(disposing);
    }

    private HudButton AddButton(string text, HudButton.Kind kind, int width, Action click) {
        var button = new HudButton {
            Text = text,
            StyleKind = kind,
            Font = HudTheme.SmallBold,
            Height = 36,
            Width = Math.Max(width, TextRenderer.MeasureText(text, HudTheme.SmallBold).Width + 32),
            Margin = new Padding(0, 0, 8, 8),
        };
        button.Click += (_, _) => click();
        _buttons.Controls.Add(button);
        return button;
    }

    private string CaptionText => _content.Title ?? Loc.F("Ask {0}", _content.ModelName);

    /// <summary>Wide enough that the longest line fits unwrapped, within the screen.</summary>
    private static int LayoutWidth(string text, Font font, Rectangle screen) {
        int longest = 0;
        foreach (string line in text.Split('\n')) {
            longest = Math.Max(longest, TextRenderer.MeasureText(line.TrimEnd('\r'), font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.ExpandTabs).Width);
        }
        int max = Math.Min(MaxLayoutWidthDip, screen.Width - 80);
        return Math.Clamp(longest + 40 + 2 + 40, Math.Min(WidthDip, max), max);
    }

    private void PlaceNearBottom() {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            Math.Max(area.Top + 10, area.Bottom - Height - 110));
    }

    private void PlaceClose() {
        _close.Location = Loc.Rtl
            ? new Point(12, (_caption.Height - _close.Height) / 2)
            : new Point(_caption.Width - _close.Width - 12, (_caption.Height - _close.Height) / 2);
    }

    private void OnCaptionPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        var dot = Loc.Mirror(new Rectangle(22, _caption.Height / 2 - 4, 8, 8), _caption.Width);
        using (var brush = new SolidBrush(HudTheme.Ink)) {
            e.Graphics.FillEllipse(brush, dot);
        }
        var title = Loc.Mirror(new Rectangle(40, 0, _caption.Width - 40 - 60, _caption.Height), _caption.Width);
        TextRenderer.DrawText(e.Graphics, CaptionText, HudTheme.Caption, title, HudTheme.Ink,
            Loc.Flags(TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));
        using var hair = new Pen(HudTheme.HairlineStrong);
        e.Graphics.DrawLine(hair, 16, _caption.Height - 1, _caption.Width - 16, _caption.Height - 1);
    }

    private void CopyText() {
        string text = _body is { SelectionLength: > 0 } ? _body.SelectedText : _text;
        if (TrySetClipboard(new DataObject(DataFormats.UnicodeText, text))) {
            Flash(_copyText, Loc.T("Copied the text."));
        }
    }

    private void CopyImage() {
        if (_content.Image == null) {
            return;
        }
        var data = new DataObject();
        data.SetImage(_content.Image);
        // Some apps (browsers, Electron) only take PNG from the clipboard.
        using var png = new MemoryStream();
        _content.Image.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, new MemoryStream(png.ToArray()));
        if (TrySetClipboard(data)) {
            Flash(_copyImage, Loc.T("Copied the image — paste it anywhere with Ctrl+V."));
        }
    }

    private void SaveImage() {
        if (_content.Image == null) {
            return;
        }
        using var dialog = new SaveFileDialog {
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg",
            FileName = "echotype-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) {
            return;
        }
        try {
            var format = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || dialog.FileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                ? ImageFormat.Jpeg : ImageFormat.Png;
            _content.Image.Save(dialog.FileName, format);
            Flash(null, Loc.T("Saved the image."));
        } catch (Exception ex) {
            Log.Write("ask popup: save failed: " + ex.Message);
            ShowError(Loc.T("Could not save the image."));
        }
    }

    private bool TrySetClipboard(DataObject data) {
        try {
            Clipboard.SetDataObject(data, copy: true, retryTimes: 6, retryDelay: 80);
            return true;
        } catch (Exception ex) {
            Log.Write("ask popup: clipboard failed: " + ex.Message);
            ShowError(Loc.T("The clipboard is busy — try again."));
            return false;
        }
    }

    private void Flash(HudButton? button, string message) {
        if (button != null) {
            button.Text = Loc.T("Copied ✓");
        }
        _status.ForeColor = HudTheme.Positive;
        _status.Text = message;
        _statusReset.Stop();
        _statusReset.Start();
    }

    private void ShowError(string message) {
        _status.ForeColor = HudTheme.Negative;
        _status.Text = message;
        _statusReset.Stop();
        _statusReset.Start();
    }

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static void OpenUrl(string url) {
        if (!IsWebUrl(url)) {
            return;
        }
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        } catch (Exception ex) {
            Log.Write("ask popup: open url failed: " + ex.Message);
        }
    }

    /// <summary>Drops inline image markdown (the image is shown separately) and extra blank lines.</summary>
    public static string CleanText(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return "";
        }
        string t = ImageMarkdown().Replace(text, "");
        t = BlankLines().Replace(t, "\n\n");
        return t.Trim();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    [GeneratedRegex(@"!\[[^\]]*\]\([^)\s]+\)")]
    private static partial Regex ImageMarkdown();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
