using EchoType.Hotkey;

namespace EchoType.Ui;

/// <summary>
/// Captures a reserved shortcut, or clears it. Single-key mode is used for Auto Enter,
/// Ask model, and the model window; chord mode (1–3 keys) for Translate.
/// </summary>
internal sealed class ShortcutPickerForm : Form {

    private readonly Settings _settings;
    private readonly HotkeyMonitor _hotkey;
    private readonly int _ignoreVk;
    private readonly bool _chordMode;
    private readonly HotkeyChord _ignoreChord;
    private readonly TextBox _hotkeyBox;
    private int _hotkeyVk;
    private HotkeyChord _chord;

    public int HotkeyVk => _hotkeyVk;

    /// <summary>The captured shortcut in chord mode (empty when cleared).</summary>
    public HotkeyChord Chord => _chord;

    /// <summary>Chord mode: one to three keys pressed together, e.g. Ctrl+Shift+T.</summary>
    public ShortcutPickerForm(
        HotkeyChord currentChord,
        HotkeyMonitor hotkey,
        Settings settings,
        string title,
        string fieldLabel,
        string hint)
        : this(0, hotkey, settings, 0, title, fieldLabel, hint, chordMode: true, currentChord: currentChord) {
    }

    public ShortcutPickerForm(
        int currentVk,
        HotkeyMonitor hotkey,
        Settings settings,
        int ignoreVk = 0,
        string? title = null,
        string? fieldLabel = null,
        string? hint = null)
        : this(currentVk, hotkey, settings, ignoreVk, title, fieldLabel, hint, chordMode: false, currentChord: default) {
    }

    private ShortcutPickerForm(
        int currentVk,
        HotkeyMonitor hotkey,
        Settings settings,
        int ignoreVk,
        string? title,
        string? fieldLabel,
        string? hint,
        bool chordMode,
        HotkeyChord currentChord) {
        _settings = settings;
        _hotkey = hotkey;
        _ignoreVk = ignoreVk != 0 ? ignoreVk : currentVk;
        _hotkeyVk = currentVk;
        _chordMode = chordMode;
        _chord = currentChord;
        _ignoreChord = currentChord;

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
            Text = CurrentLabel(),
        };
        _hotkeyBox.GotFocus += (_, _) => {
            _hotkeyBox.BackColor = Color.FromArgb(232, 242, 255);
            if (_chordMode) {
                _hotkey.ResetCapture();
                _hotkey.CaptureMaxKeys = HotkeyChord.MaxKeys;
            }
            _hotkey.CaptureKeys = true;
            if (_chordMode ? _chord.IsEmpty : _hotkeyVk == 0) {
                _hotkeyBox.Text = _chordMode ? "Press up to 3 keys…" : "Press a key…";
            }
        };
        _hotkeyBox.LostFocus += (_, _) => {
            _hotkey.CaptureKeys = false;
            _hotkey.CaptureMaxKeys = 1;
            _hotkeyBox.BackColor = SystemColors.Window;
            _hotkeyBox.Text = CurrentLabel();
        };

        var save = new Button { Text = "Save", AutoSize = true };
        var clear = new Button { Text = "Clear", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => TrySave();
        clear.Click += (_, _) => {
            _hotkeyVk = 0;
            _chord = default;
            _hotkeyBox.Text = CurrentLabel();
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
        if (_chordMode) {
            _hotkey.ChordCaptured += OnChordCaptured;
        } else {
            _hotkey.KeyCaptured += OnKeyCaptured;
        }
        _hotkeyBox.Select();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        _hotkey.CaptureKeys = false;
        _hotkey.CaptureMaxKeys = 1;
        _hotkey.KeyCaptured -= OnKeyCaptured;
        _hotkey.ChordCaptured -= OnChordCaptured;
        base.OnFormClosed(e);
    }

    private void OnKeyCaptured(uint vk) {
        if (IsDisposed || vk == 0) {
            return;
        }
        _hotkeyVk = (int)vk;
        _hotkeyBox.Text = HotkeyNames.For(_hotkeyVk);
    }

    private void OnChordCaptured(HotkeyChord chord) {
        if (IsDisposed || chord.IsEmpty) {
            return;
        }
        _chord = chord;
        _hotkeyBox.Text = HotkeyNames.For(_chord);
    }

    private void TrySave() {
        string? conflict = _chordMode
            ? HotkeyConflicts.Message(_chord, _settings, _ignoreChord)
            : HotkeyConflicts.Message(_hotkeyVk, _settings, _ignoreVk);
        if (conflict != null) {
            MessageBox.Show(this, conflict, "EchoType", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _hotkeyBox.Focus();
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private string CurrentLabel() {
        if (_chordMode) {
            return _chord.IsEmpty ? "Click here, then press up to 3 keys (or Clear)" : HotkeyNames.For(_chord);
        }
        return _hotkeyVk <= 0 ? "Click here, then press a key (or Clear)" : HotkeyNames.For(_hotkeyVk);
    }
}
