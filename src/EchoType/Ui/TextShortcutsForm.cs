namespace EchoType.Ui;

/// <summary>
/// Edit the typed text shortcuts: one row per shortcut, the trigger word → the text
/// it expands to when followed by Space in any app.
/// </summary>
internal sealed class TextShortcutsForm : Form {

    private readonly DataGridView _grid;

    public List<TextShortcut> Result { get; private set; }

    public TextShortcutsForm(IEnumerable<TextShortcut> shortcuts) {
        Result = shortcuts.Select(s => new TextShortcut { Trigger = s.Trigger, Text = s.Text }).ToList();

        Text = Loc.T("Text shortcuts");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(640, 460);
        MinimumSize = new Size(480, 340);
        Padding = new Padding(14);

        var title = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = Loc.T("Type a shortcut in any app and press Space to replace it with its text"),
            Margin = new Padding(0, 0, 0, 4),
        };
        var hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = Loc.T("e.g. وع → وعليكم السلام ورحمة الله وبركاته. A shortcut is one word (no spaces) and is "
                + "matched as a whole word, ignoring case. Shift+Enter adds a new line to the text. "
                + "Leave a row empty to remove it."),
            Padding = new Padding(0, 6, 0, 0),
        };
        _grid = new DataGridView {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersWidth = 28,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            BackgroundColor = SystemColors.Window,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "trigger",
            HeaderText = Loc.T("Shortcut"),
            FillWeight = 30,
            MaxInputLength = TextShortcut.MaxTriggerLength,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "text",
            HeaderText = Loc.T("Text to write"),
            FillWeight = 70,
            // Wrap mode makes the editing box multi-line, so Shift+Enter inserts a line break.
            DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
        });
        foreach (var shortcut in Result) {
            _grid.Rows.Add(shortcut.Trigger, shortcut.Text);
        }

        var save = new Button { Text = Loc.T("Save"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var remove = new Button { Text = Loc.T("Remove selected"), AutoSize = true };
        save.Click += (_, _) => Save();
        remove.Click += (_, _) => RemoveSelected();
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
        buttons.Controls.Add(remove);

        Controls.Add(_grid);
        Controls.Add(title);
        Controls.Add(hint);
        Controls.Add(buttons);
        Loc.ApplyTo(this);
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _grid.Select();
        int last = _grid.Rows.Count - 1;
        if (last >= 0) {
            _grid.CurrentCell = _grid.Rows[last].Cells[0];
        }
    }

    private void RemoveSelected() {
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Select(c => c.OwningRow)
            .Where(r => r is { IsNewRow: false })
            .Distinct()
            .ToList();
        foreach (var row in rows) {
            _grid.Rows.Remove(row!);
        }
    }

    private void Save() {
        _grid.EndEdit();
        var shortcuts = new List<TextShortcut>();
        var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows) {
            if (row.IsNewRow) {
                continue;
            }
            string trigger = (row.Cells[0].Value as string ?? "").Trim();
            string text = (row.Cells[1].Value as string ?? "").Trim();
            if (trigger.Length == 0 || text.Length == 0) {
                continue;
            }
            if (!TextShortcut.IsValidTrigger(trigger)) {
                Reject(row, Loc.F("\"{0}\": a shortcut must be one word without spaces.", trigger));
                return;
            }
            if (!seen.Add(trigger)) {
                Reject(row, Loc.F("\"{0}\" is listed twice.", trigger));
                return;
            }
            // Saved with plain \n line breaks; the expander converts them when it pastes.
            shortcuts.Add(new TextShortcut { Trigger = trigger, Text = text.Replace("\r\n", "\n") });
        }
        Result = shortcuts;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Reject(DataGridViewRow row, string message) {
        _grid.CurrentCell = row.Cells[0];
        MessageBox.Show(this, message, Loc.T("Text shortcuts"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
