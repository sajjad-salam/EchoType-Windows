using EchoType.Hotkey;

namespace EchoType.Ui;

/// <summary>Captures a single reserved key, or clears it. Used for Auto Enter, Ask model, and the model window.</summary>
internal sealed class ShortcutPickerForm : Form {

    private readonly Settings _settings;
    private readonly HotkeyMonitor _hotkey;
    private readonly int _ignoreVk;
    private readonly TextBox _hotkeyBox;
    private int _hotkeyVk;

    public int HotkeyVk => _hotkeyVk;

    public ShortcutPickerForm(
        int currentVk,
        HotkeyMonitor hotkey,
        Settings settings,
        int ignoreVk = 0,
        string? title = null,
        string? fieldLabel = null,
        string? hint = null) {
        _settings = settings;
        _hotkey = hotkey;
        _ignoreVk = ignoreVk != 0 ? ignoreVk : currentVk;
        _hotkeyVk = currentVk;

        Text = title ?? "EchoType — Auto Enter shortcut";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(440, 250);
        Padding = new Padding(14);

        _hotkeyBox = new TextBox {
            Dock = DockStyle.Top,
            ReadOnly = true,
            Text = HotkeyLabel(_hotkeyVk),
        };
        _hotkeyBox.GotFocus += (_, _) => {
            _hotkeyBox.BackColor = Color.FromArgb(232, 242, 255);
            _hotkey.CaptureKeys = true;
            if (_hotkeyVk == 0) {
                _hotkeyBox.Text = "Press a key…";
            }
        };
        _hotkeyBox.LostFocus += (_, _) => {
            _hotkey.CaptureKeys = false;
            _hotkeyBox.BackColor = SystemColors.Window;
            _hotkeyBox.Text = HotkeyLabel(_hotkeyVk);
        };

        var save = new Button { Text = "Save", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => TrySave();
        clear.Click += (_, _) => {
            _hotkeyVk = 0;
            _hotkeyBox.Text = HotkeyLabel(0);
        };
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
        buttons.Controls.Add(clear);

        var body = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
        };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.Controls.Add(Field(
            fieldLabel ?? "Toggle key",
            _hotkeyBox,
            hint ?? "Click the box, then press the key that should turn Auto Enter on or off. EchoType reserves that key while it is running. Clear removes the shortcut."), 0, 0);

        Controls.Add(body);
        Controls.Add(buttons);
    }

    private static TableLayoutPanel Field(string title, Control field, string hint) {
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Margin = new Padding(0),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label {
            Text = title,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0);
        panel.Controls.Add(field, 0, 1);
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
        _hotkeyBox.Select();
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
        _hotkeyVk = (int)vk;
        _hotkeyBox.Text = HotkeyNames.For(_hotkeyVk);
    }

    private void TrySave() {
        string? conflict = HotkeyConflicts.Message(_hotkeyVk, _settings, _ignoreVk);
        if (conflict != null) {
            MessageBox.Show(this, conflict, "EchoType", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _hotkeyBox.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string HotkeyLabel(int vk) =>
        vk <= 0 ? "Click here, then press a key (or Clear)" : HotkeyNames.For(vk);
}
