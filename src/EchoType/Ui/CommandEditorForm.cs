using EchoType.Hotkey;

namespace EchoType.Ui;

/// <summary>Add/edit a custom command: recording key plus one or more action buttons.</summary>
internal sealed class CommandEditorForm : Form {

    private readonly Settings _settings;
    private readonly HotkeyMonitor _hotkey;
    private readonly TextBox _nameBox;
    private readonly TextBox _hotkeyBox;
    private readonly FlowLayoutPanel _actions;
    private readonly Button _addAction;
    private HotkeyChord _chord;

    public CustomCommand Result { get; }

    public CommandEditorForm(
        CustomCommand draft,
        HotkeyMonitor hotkey,
        Settings settings) {
        Result = draft;
        _settings = settings;
        _hotkey = hotkey;
        _chord = draft.Chord;
        draft.Normalize();

        Text = string.IsNullOrWhiteSpace(draft.Name) ? "New custom command" : "Edit custom command";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(560, 560);
        MinimumSize = new Size(520, 460);
        Padding = new Padding(14);

        _nameBox = new TextBox { Dock = DockStyle.Top, Text = draft.Name };
        _hotkeyBox = new TextBox {
            Dock = DockStyle.Top,
            ReadOnly = true,
            Text = HotkeyLabel(_chord),
        };
        _hotkeyBox.GotFocus += (_, _) => {
            _hotkeyBox.BackColor = Color.FromArgb(232, 242, 255);
            _hotkey.CaptureMaxKeys = HotkeyChord.MaxKeys;
            _hotkey.CaptureKeys = true;
            if (_chord.IsEmpty) {
                _hotkeyBox.Text = "Press up to 3 keys…";
            }
        };
        _hotkeyBox.LostFocus += (_, _) => {
            _hotkey.CaptureKeys = false;
            _hotkey.CaptureMaxKeys = 1;
            _hotkeyBox.BackColor = SystemColors.Window;
            _hotkeyBox.Text = HotkeyLabel(_chord);
        };

        _actions = new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(0, 0, 8, 0),
        };

        _addAction = new Button { Text = "Add button", AutoSize = true };
        _addAction.Click += (_, _) => AddAction(new CommandButton(), focus: true);

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

        var addRow = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
        };
        addRow.Controls.Add(_addAction);

        var body = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(Field("Name", _nameBox), 0, 0);
        body.Controls.Add(Field(
            settings.ToggleRecording ? "Recording shortcut" : "Hold-to-talk shortcut",
            _hotkeyBox,
            settings.ToggleRecording
                ? "Click the box, then press one, two, or three keys (for example F8, Ctrl+Space, or Ctrl+Shift+Space). That shortcut is reserved while EchoType runs."
                : "Click the box, then hold one, two, or three keys (for example F8, Ctrl+Space, or Ctrl+Shift+Space). That shortcut is reserved while EchoType runs."), 0, 1);
        body.Controls.Add(Field(
            "Buttons",
            _actions,
            "A command can be one button or a group. After you dictate, EchoType runs a single button immediately, or shows the group so you can pick. The transcription is appended after each prompt. Put {transcript} anywhere to place it yourself. Highlighted text is included automatically (or use {selection}).",
            fill: true), 0, 2);

        Controls.Add(body);
        Controls.Add(addRow);
        Controls.Add(buttons);

        var seed = draft.Buttons.Count > 0 ? draft.Buttons : [new CommandButton { Prompt = draft.Prompt }];
        foreach (var button in seed) {
            AddAction(button, focus: false);
        }
        SyncAddEnabled();
    }

    private void AddAction(CommandButton button, bool focus) {
        if (_actions.Controls.Count >= CustomCommand.MaxButtons) {
            return;
        }
        var row = new ActionRow(button, RemoveAction);
        row.Width = Math.Max(200, _actions.ClientSize.Width - 24);
        _actions.Controls.Add(row);
        SyncAddEnabled();
        if (focus) {
            row.FocusName();
        }
    }

    private void RemoveAction(ActionRow row) {
        if (_actions.Controls.Count <= 1) {
            MessageBox.Show(this, "A command needs at least one button.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _actions.Controls.Remove(row);
        row.Dispose();
        SyncAddEnabled();
    }

    private void SyncAddEnabled() {
        _addAction.Enabled = _actions.Controls.Count < CustomCommand.MaxButtons;
        int n = 1;
        foreach (Control control in _actions.Controls) {
            if (control is ActionRow row) {
                row.SetIndex(n++);
            }
        }
    }

    private static TableLayoutPanel Field(string title, Control field, string? hint = null, bool fill = false) {
        var panel = new TableLayoutPanel {
            Dock = DockStyle.Fill,
            AutoSize = !fill,
            ColumnCount = 1,
            Margin = new Padding(0, 0, 0, 10),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        if (hint != null) {
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        panel.Controls.Add(new Label {
            Text = title,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0);
        panel.Controls.Add(field, 0, 1);
        if (hint != null) {
            panel.Controls.Add(new Label {
                Text = hint,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 4, 0, 0),
            }, 0, 2);
        }
        return panel;
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _hotkey.ChordCaptured += OnChordCaptured;
        ResizeActionRows();
        _nameBox.Select();
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        ResizeActionRows();
    }

    private void ResizeActionRows() {
        if (_actions is not { IsHandleCreated: true }) {
            return;
        }
        int width = Math.Max(200, _actions.ClientSize.Width - 24);
        foreach (Control control in _actions.Controls) {
            control.Width = width;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        _hotkey.CaptureKeys = false;
        _hotkey.CaptureMaxKeys = 1;
        _hotkey.ChordCaptured -= OnChordCaptured;
        base.OnFormClosed(e);
    }

    private void OnChordCaptured(HotkeyChord chord) {
        if (IsDisposed || chord.IsEmpty) {
            return;
        }
        _chord = chord;
        _hotkeyBox.Text = HotkeyNames.For(_chord);
    }

    private void TrySave() {
        if (_chord.IsEmpty) {
            MessageBox.Show(this, "Press a recording shortcut first (one to three keys).", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _hotkeyBox.Focus();
            return;
        }
        string? conflict = HotkeyConflicts.Message(_chord, _settings, Result.Chord);
        if (conflict != null) {
            MessageBox.Show(this, conflict, "EchoType", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _hotkeyBox.Focus();
            return;
        }

        var buttons = new List<CommandButton>();
        foreach (Control control in _actions.Controls) {
            if (control is ActionRow row) {
                var button = row.ToButton();
                if (string.IsNullOrWhiteSpace(button.Prompt)) {
                    MessageBox.Show(this, "Each button needs a prompt to send with the transcription.", "EchoType",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    row.FocusPrompt();
                    return;
                }
                buttons.Add(button);
            }
        }
        if (buttons.Count == 0) {
            MessageBox.Show(this, "Add at least one button.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Result.Name = _nameBox.Text.Trim();
        Result.SetChord(_chord);
        Result.Buttons = buttons;
        Result.Prompt = buttons[0].Prompt;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string HotkeyLabel(HotkeyChord chord) =>
        chord.IsEmpty ? "Click here, then press up to 3 keys" : HotkeyNames.For(chord);

    private sealed class ActionRow : Panel {

        private readonly Label _title;
        private readonly TextBox _nameBox;
        private readonly TextBox _promptBox;
        private readonly string _id;

        public ActionRow(CommandButton button, Action<ActionRow> remove) {
            _id = string.IsNullOrEmpty(button.Id) ? Guid.NewGuid().ToString("N") : button.Id;
            Height = 148;
            Margin = new Padding(0, 0, 0, 8);
            Padding = new Padding(8);
            BorderStyle = BorderStyle.FixedSingle;

            _title = new Label {
                AutoSize = true,
                Text = "Button",
                Margin = new Padding(0),
            };
            var removeButton = new Button {
                Text = "Remove",
                AutoSize = true,
                Margin = new Padding(0),
            };
            removeButton.Click += (_, _) => remove(this);

            var header = new TableLayoutPanel {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 0, 4),
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(_title, 0, 0);
            header.Controls.Add(removeButton, 1, 0);

            _nameBox = new TextBox {
                Dock = DockStyle.Top,
                Text = button.Name,
            };
            _promptBox = new TextBox {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                Text = button.Prompt,
            };

            var nameLabel = new Label {
                Dock = DockStyle.Top,
                AutoSize = true,
                Text = "Label",
                Margin = new Padding(0, 0, 0, 2),
            };
            var promptLabel = new Label {
                Dock = DockStyle.Top,
                AutoSize = true,
                Text = "Prompt",
                Margin = new Padding(0, 6, 0, 2),
            };

            Controls.Add(_promptBox);
            Controls.Add(promptLabel);
            Controls.Add(_nameBox);
            Controls.Add(nameLabel);
            Controls.Add(header);
        }

        public void SetIndex(int index) => _title.Text = "Button " + index;

        public void FocusName() => _nameBox.Select();

        public void FocusPrompt() => _promptBox.Select();

        public CommandButton ToButton() => new() {
            Id = _id,
            Name = _nameBox.Text.Trim(),
            Prompt = _promptBox.Text.TrimEnd(),
        };
    }
}
