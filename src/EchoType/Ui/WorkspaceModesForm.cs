namespace EchoType.Ui;

/// <summary>
/// Edit the spoken workspace modes: one row per mode with the shortcut EchoType presses and the
/// words that mean it. A test box shows which mode a phrase would trigger.
/// </summary>
internal sealed class WorkspaceModesForm : Form {

    private readonly DataGridView _grid;
    private readonly TextBox _testBox;
    private readonly Label _testResult;

    public List<WorkspaceMode> Result { get; private set; }

    public WorkspaceModesForm(IEnumerable<WorkspaceMode> modes) {
        Result = modes.Select(Clone).ToList();

        Text = "Workspace modes";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(820, 480);
        MinimumSize = new Size(560, 360);
        Padding = new Padding(14);

        var title = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = "Say a mode (e.g. \"وضع البرمجة\", \"coding mode\", \"start coding\") and EchoType presses its shortcut instead of pasting",
            Margin = new Padding(0, 0, 0, 4),
        };
        var hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Shortcut: e.g. Ctrl+Shift+F6. Keywords: words or short phrases, comma separated, any language — "
                + "the mode name counts too. Only short dictations (up to 8 words) are treated as commands. "
                + "Leave a row's name empty or use Remove to delete it.",
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Mode", FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "shortcut", HeaderText = "Shortcut to press", FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "keywords", HeaderText = "Keywords (comma separated)", FillWeight = 60 });
        FillGrid(Result);

        _testBox = new TextBox { Width = 320, PlaceholderText = "Try a phrase, e.g. ابدأ البرمجة" };
        _testResult = new Label { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
        _testBox.TextChanged += (_, _) => UpdateTest();
        _grid.CellEndEdit += (_, _) => UpdateTest();
        var testRow = new FlowLayoutPanel {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 8),
        };
        testRow.Controls.Add(new Label { Text = "Test:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        testRow.Controls.Add(_testBox);
        testRow.Controls.Add(_testResult);

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var remove = new Button { Text = "Remove selected", AutoSize = true };
        var defaults = new Button { Text = "Restore defaults", AutoSize = true };
        save.Click += (_, _) => Save();
        remove.Click += (_, _) => RemoveSelected();
        defaults.Click += (_, _) => {
            FillGrid(WorkspaceMode.Defaults());
            UpdateTest();
        };
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
        buttons.Controls.Add(defaults);

        Controls.Add(_grid);
        Controls.Add(testRow);
        Controls.Add(title);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    private static WorkspaceMode Clone(WorkspaceMode m) =>
        new() { Name = m.Name, Shortcut = m.Shortcut, Keywords = [.. m.Keywords] };

    private void FillGrid(IEnumerable<WorkspaceMode> modes) {
        _grid.Rows.Clear();
        foreach (var mode in modes) {
            _grid.Rows.Add(mode.Name, mode.Shortcut, string.Join(", ", mode.Keywords));
        }
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
        UpdateTest();
    }

    /// <summary>Reads the grid; <paramref name="error"/> names the first row with a bad shortcut.</summary>
    private List<WorkspaceMode> ReadGrid(out string? error) {
        error = null;
        var modes = new List<WorkspaceMode>();
        foreach (DataGridViewRow row in _grid.Rows) {
            if (row.IsNewRow) {
                continue;
            }
            string name = (row.Cells[0].Value as string ?? "").Trim();
            string shortcut = (row.Cells[1].Value as string ?? "").Trim();
            string keywords = row.Cells[2].Value as string ?? "";
            if (name.Length == 0) {
                continue;
            }
            string formatted = WorkspaceMode.FormatShortcut(shortcut);
            if (formatted.Length == 0) {
                error ??= "\"" + name + "\": shortcut \"" + shortcut + "\" is not valid. Use e.g. Ctrl+Shift+F6.";
                formatted = shortcut;
            }
            modes.Add(new WorkspaceMode {
                Name = name,
                Shortcut = formatted,
                Keywords = keywords.Split(new[] { ',', '،', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct()
                    .ToList(),
            });
        }
        return modes;
    }

    private void UpdateTest() {
        string phrase = _testBox.Text.Trim();
        if (phrase.Length == 0) {
            _testResult.Text = "";
            return;
        }
        var match = WorkspaceMode.Match(phrase, ReadGrid(out _));
        _testResult.ForeColor = match == null ? SystemColors.GrayText : Color.SeaGreen;
        _testResult.Text = match == null
            ? "→ no mode (would paste as text)"
            : "→ " + match.Name + " (" + match.Shortcut + ")";
    }

    private void Save() {
        _grid.EndEdit();
        var modes = ReadGrid(out string? error);
        if (error != null) {
            MessageBox.Show(this, error, "EchoType", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Result = modes;
        DialogResult = DialogResult.OK;
        Close();
    }
}
