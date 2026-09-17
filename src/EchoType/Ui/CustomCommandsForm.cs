using EchoType.Hotkey;

namespace EchoType.Ui;

/// <summary>Lists custom commands and opens the add/edit dialog.</summary>
internal sealed class CustomCommandsForm : Form {

    private readonly Settings _settings;
    private readonly HotkeyMonitor _hotkey;
    private readonly Action _onChanged;
    private readonly ListView _list;

    public CustomCommandsForm(Settings settings, HotkeyMonitor hotkey, string modelName, Action onChanged) {
        _settings = settings;
        _hotkey = hotkey;
        _onChanged = onChanged;

        // Create the list before any size change — ClientSize/Font fire OnResize.
        _list = new ListView {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Name", 160);
        _list.Columns.Add("Shortcut", 180);
        _list.Columns.Add("Buttons", 320);
        _list.DoubleClick += (_, _) => EditSelected();
        _list.KeyDown += OnListKeyDown;

        Text = "EchoType — Custom Commands";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(640, 380);
        MinimumSize = new Size(520, 300);
        Padding = new Padding(14);

        var add = new Button { Text = "Add…", AutoSize = true };
        var edit = new Button { Text = "Edit…", AutoSize = true };
        var delete = new Button { Text = "Delete", AutoSize = true };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
        add.Click += (_, _) => AddCommand();
        edit.Click += (_, _) => EditSelected();
        delete.Click += (_, _) => DeleteSelected();
        CancelButton = close;

        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(add);
        buttons.Controls.Add(edit);
        buttons.Controls.Add(delete);
        var spacer = new Panel { Width = 16, Height = 1 };
        buttons.Controls.Add(spacer);
        buttons.Controls.Add(close);

        var hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Commands are shared by ChatGPT and Gemini. "
                + (settings.ToggleRecording
                    ? "Press a command’s shortcut to start, press it again to stop. "
                    : "Hold a command’s shortcut to dictate. ")
                + "A command can be one button or a group of buttons; after dictation EchoType runs a single button or lets you pick. Then it pastes "
                + modelName + "’s reply.",
            Padding = new Padding(0, 8, 0, 0),
        };

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);

        Reload();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        ResizeColumns();
    }

    protected override void OnResize(EventArgs e) {
        base.OnResize(e);
        ResizeColumns();
    }

    private void ResizeColumns() {
        if (_list is not { IsHandleCreated: true, Columns.Count: >= 3 }) {
            return;
        }
        int promptWidth = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 8;
        _list.Columns[2].Width = Math.Max(120, promptWidth);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e) {
        if (e.KeyCode == Keys.Insert) {
            AddCommand();
            e.Handled = true;
        } else if (e.KeyCode == Keys.Delete) {
            DeleteSelected();
            e.Handled = true;
        } else if (e.KeyCode == Keys.Enter) {
            EditSelected();
            e.Handled = true;
        }
    }

    private void Reload() {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var cmd in _settings.ActiveCommands) {
            cmd.Normalize();
            var item = new ListViewItem(cmd.DisplayName) { Tag = cmd };
            item.SubItems.Add(HotkeyNames.For(cmd.Chord));
            item.SubItems.Add(Preview(cmd));
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private void AddCommand() {
        try {
            var draft = new CustomCommand();
            using var editor = new CommandEditorForm(draft, _hotkey, _settings);
            if (editor.ShowDialog(this) != DialogResult.OK) {
                return;
            }
            _settings.ActiveCommands.Add(editor.Result);
            Persist();
            Reload();
            SelectId(editor.Result.Id);
        } catch (Exception ex) {
            Log.Write("commands ui: add failed: " + ex);
            MessageBox.Show(this, "Could not add a custom command: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void EditSelected() {
        if (SelectedCommand() is not { } cmd) {
            return;
        }
        try {
            var draft = cmd.Clone();
            using var editor = new CommandEditorForm(draft, _hotkey, _settings);
            if (editor.ShowDialog(this) != DialogResult.OK) {
                return;
            }
            cmd.CopyFrom(draft);
            Persist();
            Reload();
            SelectId(cmd.Id);
        } catch (Exception ex) {
            Log.Write("commands ui: edit failed: " + ex);
            MessageBox.Show(this, "Could not edit that custom command: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteSelected() {
        if (SelectedCommand() is not { } cmd) {
            return;
        }
        var result = MessageBox.Show(this,
            $"Delete “{cmd.DisplayName}”?",
            "EchoType", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) {
            return;
        }
        _settings.ActiveCommands.Remove(cmd);
        Persist();
        Reload();
    }

    private CustomCommand? SelectedCommand() =>
        _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as CustomCommand;

    private void SelectId(string id) {
        foreach (ListViewItem item in _list.Items) {
            if (item.Tag is CustomCommand cmd && cmd.Id == id) {
                item.Selected = true;
                item.Focused = true;
                item.EnsureVisible();
                break;
            }
        }
    }

    private void Persist() {
        _settings.Save();
        _onChanged();
    }

    private static string Preview(CustomCommand cmd) {
        var buttons = cmd.ResolvedButtons;
        if (buttons.Count == 0) {
            return "";
        }
        if (buttons.Count == 1) {
            return PreviewPrompt(buttons[0].Prompt);
        }
        string names = string.Join(", ", buttons.Select(b => b.DisplayName));
        return buttons.Count + " buttons — " + names;
    }

    private static string PreviewPrompt(string prompt) {
        string oneLine = (prompt ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 80 ? oneLine : oneLine[..80] + "…";
    }
}
