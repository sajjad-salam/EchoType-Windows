namespace EchoType.Ui;

/// <summary>
/// Edit the manual word fixes applied to every transcript (e.g. dialect spellings).
/// One row per word: what the model writes → what EchoType should paste instead.
/// </summary>
internal sealed class WordReplacementsForm : Form {

    private readonly DataGridView _grid;

    public List<WordReplacement> Result { get; private set; }

    public WordReplacementsForm(IEnumerable<WordReplacement> rules) {
        Result = rules.Select(r => new WordReplacement { From = r.From, To = r.To }).ToList();

        Text = "Word replacements";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(560, 420);
        MinimumSize = new Size(440, 320);
        Padding = new Padding(14);

        var title = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "Replace words in every transcript before it is pasted",
            Margin = new Padding(0, 0, 0, 4),
        };
        var hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Whole words only, e.g. نقول → نكول. Applied to dictation, Auto Clean, Translate, "
                + "Ask model and custom commands. Leave a row empty to remove it.",
            Padding = new Padding(0, 6, 0, 0),
        };
        _grid = new DataGridView {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            RowHeadersWidth = 28,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "from", HeaderText = "Model writes" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "to", HeaderText = "Replace with" });
        foreach (var rule in Result) {
            _grid.Rows.Add(rule.From, rule.To);
        }

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
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
        var rules = new List<WordReplacement>();
        foreach (DataGridViewRow row in _grid.Rows) {
            if (row.IsNewRow) {
                continue;
            }
            string from = (row.Cells[0].Value as string ?? "").Trim();
            string to = (row.Cells[1].Value as string ?? "").Trim();
            if (from.Length == 0) {
                continue;
            }
            rules.Add(new WordReplacement { From = from, To = to });
        }
        Result = rules;
        DialogResult = DialogResult.OK;
        Close();
    }
}
