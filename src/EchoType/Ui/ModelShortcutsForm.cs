using EchoType.Hotkey;

namespace EchoType.Ui;

/// <summary>Assigns tap keys that switch the transcription model.</summary>
internal sealed class ModelShortcutsForm : Form {

    private readonly Settings _settings;
    private readonly HotkeyMonitor _hotkey;
    private readonly TextBox _chatgptBox;
    private readonly TextBox _geminiBox;
    private int _chatgptVk;
    private int _geminiVk;

    public ModelShortcutsForm(Settings settings, HotkeyMonitor hotkey) {
        _settings = settings;
        _hotkey = hotkey;
        _chatgptVk = settings.ChatGptSwitchVk;
        _geminiVk = settings.GeminiSwitchVk;

        Text = "EchoType — Model shortcuts";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(460, 340);
        Padding = new Padding(14);

        _chatgptBox = CaptureBox(() => _chatgptVk);
        _geminiBox = CaptureBox(() => _geminiVk);
        WireCapture(_chatgptBox, vk => {
            _chatgptVk = vk;
            _chatgptBox.Text = HotkeyLabel(vk);
        });
        WireCapture(_geminiBox, vk => {
            _geminiVk = vk;
            _geminiBox.Text = HotkeyLabel(vk);
        });

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => TrySave();
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);

        var body = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(Field(
            "Switch to ChatGPT",
            _chatgptBox,
            () => {
                _chatgptVk = 0;
                _chatgptBox.Text = HotkeyLabel(0);
            },
            "Click the box, then press the key. A tap switches to ChatGPT. Clear removes the shortcut."), 0, 0);
        body.Controls.Add(Field(
            "Switch to Gemini",
            _geminiBox,
            () => {
                _geminiVk = 0;
                _geminiBox.Text = HotkeyLabel(0);
            },
            "Click the box, then press the key. A tap switches to Gemini. Custom commands keep using whichever model is selected."), 0, 1);

        Controls.Add(body);
        Controls.Add(buttons);
    }

    private TextBox CaptureBox(Func<int> current) => new() {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        Text = HotkeyLabel(current()),
    };

    private void WireCapture(TextBox box, Action<int> assign) {
        box.GotFocus += (_, _) => {
            box.BackColor = Color.FromArgb(232, 242, 255);
            _hotkey.CaptureKeys = true;
            if ((box == _chatgptBox ? _chatgptVk : _geminiVk) == 0) {
                box.Text = "Press a key…";
            }
        };
        box.LostFocus += (_, _) => {
            _hotkey.CaptureKeys = false;
            box.BackColor = SystemColors.Window;
            box.Text = HotkeyLabel(box == _chatgptBox ? _chatgptVk : _geminiVk);
        };
        box.Tag = assign;
    }

    private static TableLayoutPanel Field(string title, TextBox field, Action clear, string hint) {
        var row = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Margin = new Padding(0, 0, 8, 0);
        row.Controls.Add(field, 0, 0);
        var clearButton = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(0) };
        clearButton.Click += (_, _) => clear();
        row.Controls.Add(clearButton, 1, 0);

        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 12),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label {
            Text = title,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        panel.Controls.Add(row, 0, 1);
        panel.Controls.Add(new Label {
            Text = hint,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0),
        }, 0, 2);
        return panel;
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _hotkey.KeyCaptured += OnKeyCaptured;
        _chatgptBox.Select();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        _hotkey.CaptureKeys = false;
        _hotkey.KeyCaptured -= OnKeyCaptured;
        base.OnFormClosed(e);
    }

    private void OnKeyCaptured(uint vk) {
        if (IsDisposed || vk == 0) {
            return;
        }
        if (_chatgptBox.Focused && _chatgptBox.Tag is Action<int> assignChat) {
            assignChat((int)vk);
            return;
        }
        if (_geminiBox.Focused && _geminiBox.Tag is Action<int> assignGemini) {
            assignGemini((int)vk);
        }
    }

    private void TrySave() {
        if (_chatgptVk != 0 && _chatgptVk == _geminiVk) {
            MessageBox.Show(this, "ChatGPT and Gemini cannot share the same shortcut.",
                "EchoType", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _chatgptBox.Focus();
            return;
        }

        int[] ignore = [_settings.ChatGptSwitchVk, _settings.GeminiSwitchVk];
        if (ShowConflict(_chatgptVk, ignore, _chatgptBox)
            || ShowConflict(_geminiVk, ignore, _geminiBox)) {
            return;
        }

        _settings.ChatGptSwitchVk = _chatgptVk;
        _settings.GeminiSwitchVk = _geminiVk;
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool ShowConflict(int vk, int[] ignore, Control focus) {
        string? message = HotkeyConflicts.Message(vk, _settings, ignore);
        if (message == null) {
            return false;
        }
        MessageBox.Show(this, message, "EchoType", MessageBoxButtons.OK, MessageBoxIcon.Information);
        focus.Focus();
        return true;
    }

    private static string HotkeyLabel(int vk) =>
        vk <= 0 ? "Click here, then press a key (or Clear)" : HotkeyNames.For(vk);
}
