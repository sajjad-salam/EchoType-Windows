namespace EchoType.Ui;

/// <summary>
/// Find-and-replace rule editor for text produced by every EchoType workflow.
/// Rules match whole words or phrases, ignoring case, before text is pasted.
/// </summary>
internal sealed class WordReplacementsForm : Form {

    private const int FindColumn = 0;
    private const int ReplaceColumn = 1;
    private const int RemoveColumn = 2;

    private readonly DataGridView _grid;
    private readonly TextBox _search;
    private readonly Label _count;
    private readonly Label _empty;
    private readonly Label _error;

    public List<WordReplacement> Result { get; private set; }

    public WordReplacementsForm(IEnumerable<WordReplacement> rules) {
        Result = rules.Select(r => new WordReplacement { From = r.From, To = r.To }).ToList();

        Text = Loc.T("Find & replace");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(560, 420);
        Padding = new Padding(24);

        var title = DialogTheme.Heading(Loc.T("Find & replace"));
        var subtitle = DialogTheme.SupportingText(
            Loc.T("Correct names, dialect words, or phrases automatically before EchoType pastes your text."));
        var header = new TableLayoutPanel {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 18),
        };
        header.Controls.Add(title, 0, 0);
        header.Controls.Add(subtitle, 0, 1);

        _search = new TextBox {
            Dock = DockStyle.Fill,
            PlaceholderText = Loc.T("Search rules…"),
            Margin = new Padding(0, 4, 12, 4),
        };
        _search.TextChanged += (_, _) => ApplyFilter();
        _count = new Label {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = HudTheme.InkSoft,
            Font = HudTheme.Small,
            Margin = new Padding(0, 0, 14, 0),
        };
        var add = new Button {
            Text = Loc.T("Add rule"),
            AutoSize = true,
            Margin = new Padding(0),
        };
        add.Click += (_, _) => AddRule();
        var toolbar = new TableLayoutPanel {
            Dock = DockStyle.Top,
            Height = 52,
            ColumnCount = 3,
            Margin = new Padding(0),
            Padding = new Padding(0, 6, 0, 10),
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.Controls.Add(_search, 0, 0);
        toolbar.Controls.Add(_count, 1, 0);
        toolbar.Controls.Add(add, 2, 0);

        _grid = new DataGridView {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            MultiSelect = true,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "find",
            HeaderText = Loc.T("Find"),
            FillWeight = 45,
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "replace",
            HeaderText = Loc.T("Replace with"),
            FillWeight = 45,
        });
        _grid.Columns.Add(new DataGridViewButtonColumn {
            Name = "remove",
            HeaderText = "",
            Text = Loc.T("Remove"),
            UseColumnTextForButtonValue = true,
            FillWeight = 10,
            MinimumWidth = 88,
            FlatStyle = FlatStyle.Flat,
        });
        foreach (var rule in Result) {
            _grid.Rows.Add(rule.From, rule.To);
        }
        _grid.CellContentClick += (_, e) => {
            if (e.RowIndex >= 0 && e.ColumnIndex == RemoveColumn) {
                RemoveRow(_grid.Rows[e.RowIndex]);
            }
        };
        _grid.CellEndEdit += (_, _) => {
            if (_error != null) {
                _error.Text = "";
            }
            ApplyFilter();
        };
        _grid.RowsAdded += (_, _) => UpdateState();
        _grid.RowsRemoved += (_, _) => UpdateState();

        _empty = new Label {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = HudTheme.InkMuted,
            BackColor = HudTheme.Surface,
            Text = Loc.T("No replacement rules yet. Add one to get started."),
            Visible = false,
        };
        _grid.Controls.Add(_empty);
        _grid.Resize += (_, _) => CenterEmptyState();

        var save = new Button { Text = Loc.T("Save changes"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var removeSelected = new Button { Text = Loc.T("Remove selected"), AutoSize = true };
        save.Click += (_, _) => Save();
        removeSelected.Click += (_, _) => RemoveSelected();
        CancelButton = cancel;

        _error = new Label {
            AutoSize = true,
            ForeColor = HudTheme.Negative,
            Font = HudTheme.SmallBold,
            Margin = new Padding(0, 7, 12, 0),
        };
        var actions = new FlowLayoutPanel {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0),
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        actions.Controls.Add(removeSelected);
        actions.Controls.Add(_error);

        var hint = new Label {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = HudTheme.InkSoft,
            Font = HudTheme.Small,
            Text = Loc.T("Matches whole words or phrases, ignoring case. Leave Replace with empty to remove the matched text."),
            Margin = new Padding(0, 0, 0, 12),
        };
        var footer = new TableLayoutPanel {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(0, 16, 0, 0),
        };
        footer.Controls.Add(hint, 0, 0);
        footer.Controls.Add(actions, 0, 1);

        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(header);
        Controls.Add(footer);
        Loc.ApplyTo(this);
        DialogTheme.Apply(this);
        DialogTheme.Primary(add);
        DialogTheme.Primary(save);
        DialogTheme.Danger(removeSelected);
        UpdateState();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        if (_grid.Rows.Cast<DataGridViewRow>().Any(row => !row.IsNewRow)) {
            _search.Select();
        } else {
            AddRule();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.Control && e.KeyCode == Keys.F) {
            _search.Select();
            _search.SelectAll();
            e.SuppressKeyPress = true;
        } else if (e.Control && e.KeyCode == Keys.N) {
            AddRule();
            e.SuppressKeyPress = true;
        } else if (e.KeyCode == Keys.Delete && !_grid.IsCurrentCellInEditMode && _grid.ContainsFocus) {
            RemoveSelected();
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    private void AddRule() {
        _search.Clear();
        int rowIndex = _grid.Rows.Add();
        _grid.CurrentCell = _grid.Rows[rowIndex].Cells[FindColumn];
        _grid.BeginEdit(true);
        UpdateState();
    }

    private void RemoveSelected() {
        var rows = _grid.SelectedCells.Cast<DataGridViewCell>()
            .Select(cell => cell.OwningRow)
            .Where(row => row is { IsNewRow: false })
            .Distinct()
            .ToList();
        foreach (var row in rows) {
            _grid.Rows.Remove(row!);
        }
        UpdateState();
    }

    private void RemoveRow(DataGridViewRow row) {
        if (!row.IsNewRow) {
            _grid.Rows.Remove(row);
            UpdateState();
        }
    }

    private void ApplyFilter() {
        string query = _search.Text.Trim();
        _grid.CurrentCell = null;
        foreach (DataGridViewRow row in _grid.Rows) {
            if (row.IsNewRow) {
                continue;
            }
            string find = row.Cells[FindColumn].Value as string ?? "";
            string replace = row.Cells[ReplaceColumn].Value as string ?? "";
            row.Visible = query.Length == 0
                || find.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || replace.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }
        UpdateState();
    }

    private void UpdateState() {
        int total = _grid.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow);
        int visible = _grid.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow && row.Visible);
        _count.Text = _search.TextLength > 0
            ? Loc.F("{0} of {1} rules", visible, total)
            : Loc.F("{0} rules", total);
        _empty.Text = total == 0
            ? Loc.T("No replacement rules yet. Add one to get started.")
            : Loc.T("No rules match your search.");
        _empty.Visible = visible == 0;
        CenterEmptyState();
    }

    private void CenterEmptyState() {
        _empty.Location = new Point(
            Math.Max(0, (_grid.ClientSize.Width - _empty.Width) / 2),
            Math.Max(_grid.ColumnHeadersHeight + 24, (_grid.ClientSize.Height - _empty.Height) / 2));
        _empty.BringToFront();
    }

    private void Save() {
        _grid.EndEdit();
        _error.Text = "";
        foreach (DataGridViewRow row in _grid.Rows) {
            row.ErrorText = "";
        }

        var rules = new List<WordReplacement>();
        var seen = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        foreach (DataGridViewRow row in _grid.Rows) {
            if (row.IsNewRow) {
                continue;
            }
            string find = (row.Cells[FindColumn].Value as string ?? "").Trim();
            string replace = (row.Cells[ReplaceColumn].Value as string ?? "").Trim();
            if (find.Length == 0 && replace.Length == 0) {
                continue;
            }
            if (find.Length == 0) {
                Reject(row, Loc.T("Enter the word or phrase to find."));
                return;
            }
            if (!seen.Add(find)) {
                Reject(row, Loc.F("\"{0}\" is listed twice.", find));
                return;
            }
            rules.Add(new WordReplacement { From = find, To = replace });
        }

        Result = rules;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Reject(DataGridViewRow row, string message) {
        _search.Clear();
        row.Visible = true;
        row.ErrorText = message;
        _grid.CurrentCell = row.Cells[FindColumn];
        _grid.Select();
        _error.Text = message;
    }
}
