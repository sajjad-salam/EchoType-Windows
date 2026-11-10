using EchoType.History;

namespace EchoType.Ui;

/// <summary>
/// Everything EchoType pasted or showed, newest first: search it, copy or paste an entry
/// again, pick a misheard word in it and add a word replacement, or delete entries.
/// One window at a time; opening it again brings it to the front.
/// </summary>
internal sealed class HistoryForm : Form {

    private static HistoryForm? _current;

    private readonly Action<string> _paste;
    private readonly Action<string> _addWordFix;
    private readonly TextBox _search;
    private readonly DataGridView _grid;
    private readonly Label _meta;
    private readonly TextBox _result;
    private readonly Label _transcriptLabel;
    private readonly TextBox _transcript;
    private readonly Label _empty;
    private readonly Button _copy;
    private readonly Button _pasteButton;
    private readonly Button _addWord;
    private readonly Button _delete;
    private readonly Label _status;
    private readonly TableLayoutPanel _layout;
    private readonly System.Windows.Forms.Timer _statusReset;
    private List<HistoryEntry> _shown = [];

    /// <summary>Shows the History window (or brings the open one to the front).</summary>
    /// <param name="paste">Pastes text into the app that was in front before the window opened.</param>
    /// <param name="addWordFix">Opens the quick word-replacement popup for a word.</param>
    public static void ShowWindow(Action<string> paste, Action<string> addWordFix) {
        if (_current is { IsDisposed: false } open) {
            if (open.WindowState == FormWindowState.Minimized) {
                open.WindowState = FormWindowState.Normal;
            }
            open.Show();
            open.Activate();
            return;
        }
        _current = new HistoryForm(paste, addWordFix);
        _current.Show();
        _current.Activate();
    }

    private HistoryForm(Action<string> paste, Action<string> addWordFix) {
        _paste = paste;
        _addWordFix = addWordFix;

        Text = Loc.T("EchoType history");
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = true;
        MaximizeBox = true;
        Size = new Size(900, 640);
        MinimumSize = new Size(620, 460);
        Padding = new Padding(18);
        KeyPreview = true;

        _search = new TextBox {
            Dock = DockStyle.Fill,
            PlaceholderText = Loc.T("Search what you said, replies and apps…"),
        };
        _search.TextChanged += (_, _) => Reload();

        var clearAll = new Button { Text = Loc.T("Clear history…"), AutoSize = true, Anchor = AnchorStyles.Right };
        clearAll.Click += (_, _) => ClearAll();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 0, 0, 10) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_search, 0, 0);
        top.Controls.Add(clearAll, 1, 0);

        _grid = new DataGridView {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            RowHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "time", HeaderText = Loc.T("When"), FillWeight = 18 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = Loc.T("What"), FillWeight = 16 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "app", HeaderText = Loc.T("App"), FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "text", HeaderText = Loc.T("Text"), FillWeight = 52 });
        _grid.SelectionChanged += (_, _) => ShowSelected();
        _grid.CellDoubleClick += (_, _) => Copy();
        _grid.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Delete) {
                DeleteSelected();
                e.Handled = true;
            } else if (e.KeyCode == Keys.Enter) {
                PasteSelected();
                e.Handled = true;
            }
        };

        _empty = new Label {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Visible = false,
        };

        var listHost = new Panel { Dock = DockStyle.Fill };
        listHost.Controls.Add(_grid);
        listHost.Controls.Add(_empty);

        _meta = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 10, 0, 6) };
        _result = new TextBox {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            WordWrap = true,
        };
        _result.TextChanged += (_, _) => ApplyDirection(_result);
        _transcriptLabel = new Label {
            AutoSize = true,
            Text = Loc.T("What you said"),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 4),
        };
        _transcript = new TextBox {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            WordWrap = true,
        };
        _transcript.TextChanged += (_, _) => ApplyDirection(_transcript);

        _copy = new Button { Text = Loc.T("Copy"), AutoSize = true };
        _copy.Click += (_, _) => Copy();
        _pasteButton = new Button { Text = Loc.T("Paste into previous app"), AutoSize = true };
        _pasteButton.Click += (_, _) => PasteSelected();
        _addWord = new Button { Text = Loc.T("Fix a word…"), AutoSize = true };
        _addWord.Click += (_, _) => AddWordFix();
        _delete = new Button { Text = Loc.T("Delete"), AutoSize = true };
        _delete.Click += (_, _) => DeleteSelected();
        _status = new Label { AutoSize = true, ForeColor = HudTheme.Positive, Margin = new Padding(12, 10, 0, 0) };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(_copy);
        buttons.Controls.Add(_pasteButton);
        buttons.Controls.Add(_addWord);
        buttons.Controls.Add(_delete);
        buttons.Controls.Add(_status);

        var layout = _layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(listHost, 0, 0);
        layout.Controls.Add(_meta, 0, 1);
        layout.Controls.Add(_result, 0, 2);
        layout.Controls.Add(_transcriptLabel, 0, 3);
        layout.Controls.Add(_transcript, 0, 4);
        layout.Controls.Add(buttons, 0, 5);

        Controls.Add(layout);
        Controls.Add(top);

        _statusReset = new System.Windows.Forms.Timer { Interval = 2200 };
        _statusReset.Tick += (_, _) => {
            _statusReset.Stop();
            _status.Text = "";
        };

        Loc.ApplyTo(this);
        DialogTheme.Apply(this);
        TranscriptHistory.Changed += OnHistoryChanged;
        Reload();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _search.Select();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape) {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.F) {
            _search.Select();
            _search.SelectAll();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) {
        TranscriptHistory.Changed -= OnHistoryChanged;
        if (ReferenceEquals(_current, this)) {
            _current = null;
        }
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _statusReset.Dispose();
            _arabicFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnHistoryChanged() {
        if (IsDisposed) {
            return;
        }
        if (InvokeRequired) {
            BeginInvoke(OnHistoryChanged);
            return;
        }
        Reload();
    }

    private HistoryEntry? Selected =>
        _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Tag is HistoryEntry e ? e : null;

    private void Reload() {
        string keepId = Selected?.Id ?? "";
        string q = _search.Text.Trim();
        _shown = TranscriptHistory.Entries
            .Where(e => q.Length == 0
                || e.Result.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || e.Transcript.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || e.App.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                || TranscriptHistory.KindLabel(e.Kind).Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        _grid.SuspendLayout();
        _grid.Rows.Clear();
        int select = 0;
        for (int i = 0; i < _shown.Count; i++) {
            var e = _shown[i];
            string preview = e.Text.Replace("\r", " ").Replace("\n", " ");
            if (preview.Length > 160) {
                preview = preview[..160] + "…";
            }
            int row = _grid.Rows.Add(FormatTime(e.Time),
                TranscriptHistory.KindLabel(e.Kind) + (e.Failed ? " ⚠" : ""),
                e.App, preview);
            _grid.Rows[row].Tag = e;
            if (e.Id == keepId) {
                select = row;
            }
        }
        _grid.ResumeLayout();

        bool any = _shown.Count > 0;
        _grid.Visible = any;
        _empty.Visible = !any;
        _empty.Text = TranscriptHistory.Entries.Count == 0
            ? Loc.T("Nothing yet. Every dictation, reply and translation shows up here.")
            : Loc.T("No entries match your search.");
        if (any) {
            _grid.ClearSelection();
            _grid.Rows[select].Selected = true;
            _grid.CurrentCell = _grid.Rows[select].Cells[0];
        }
        ShowSelected();
    }

    private void ShowSelected() {
        var e = Selected;
        bool has = e != null;
        _copy.Enabled = _pasteButton.Enabled = _addWord.Enabled = _delete.Enabled = has;
        if (e == null) {
            _meta.Text = "";
            _result.Text = "";
            _transcript.Text = "";
            _transcriptLabel.Visible = _transcript.Visible = false;
            _layout.RowStyles[4] = new RowStyle(SizeType.Percent, 0);
            return;
        }
        var parts = new List<string> { e.Time.ToString("f"), TranscriptHistory.KindLabel(e.Kind) };
        if (e.App.Length > 0) parts.Add(e.App);
        if (e.Model.Length > 0) parts.Add(e.Model);
        if (e.Failed) parts.Add(Loc.T("failed — this is the transcript"));
        if (e.Note.Length > 0) parts.Add(e.Note);
        _meta.Text = string.Join("  ·  ", parts);
        _result.Text = e.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        bool showTranscript = e.Result.Length > 0 && e.Transcript.Length > 0
            && !string.Equals(e.Transcript.Trim(), e.Result.Trim(), StringComparison.Ordinal);
        _transcriptLabel.Visible = _transcript.Visible = showTranscript;
        _transcript.Text = showTranscript ? e.Transcript.Replace("\r\n", "\n").Replace("\n", "\r\n") : "";
        // Without a separate transcript the reply box takes its space.
        _layout.RowStyles[4] = new RowStyle(SizeType.Percent, showTranscript ? 20 : 0);
    }

    private Font? _arabicFont;
    private Font? _latinFont;

    /// <summary>Arabic text reads right to left in the Arabic font; anything else left to right.</summary>
    private void ApplyDirection(TextBox box) {
        _latinFont ??= box.Font;
        bool rtl = ArabicFont.StartsRightToLeft(box.Text);
        box.RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
        if (rtl) {
            _arabicFont ??= ArabicFont.Create(_latinFont.SizeInPoints);
        }
        box.Font = rtl && _arabicFont != null ? _arabicFont : _latinFont;
    }

    private static string FormatTime(DateTime t) {
        DateTime now = DateTime.Now;
        if (t.Date == now.Date) return t.ToString("t");
        if (t.Date == now.Date.AddDays(-1)) return Loc.T("Yesterday") + " " + t.ToString("t");
        return t.ToString("g");
    }

    /// <summary>The selected part of the shown text, or all of it.</summary>
    private string SelectedText() {
        var box = _transcript.Focused && _transcript.SelectionLength > 0 ? _transcript : _result;
        return box.SelectionLength > 0 ? box.SelectedText : Selected?.Text ?? "";
    }

    private void Copy() {
        string text = SelectedText();
        if (text.Length == 0) return;
        try {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true, retryTimes: 6, retryDelay: 80);
            Flash(Loc.T("Copied."));
        } catch (Exception ex) {
            Log.Write("history: copy failed: " + ex.Message);
            Flash(Loc.T("The clipboard is busy — try again."), error: true);
        }
    }

    private void PasteSelected() {
        string text = SelectedText();
        if (text.Length == 0) return;
        // Step out of the way so the app behind becomes the foreground window, then paste there.
        WindowState = FormWindowState.Minimized;
        _paste(text);
    }

    private void AddWordFix() {
        string word = WordReplacement.WordFromSelection(
            _result.SelectionLength > 0 ? _result.SelectedText
            : _transcript.SelectionLength > 0 ? _transcript.SelectedText : "") ?? "";
        _addWordFix(word);
    }

    private void DeleteSelected() {
        if (Selected is { } e) {
            TranscriptHistory.Remove(e.Id);
        }
    }

    private void ClearAll() {
        if (TranscriptHistory.Entries.Count == 0) return;
        if (MessageBox.Show(this, Loc.T("Delete every History entry? This can't be undone."), "EchoType",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK) {
            TranscriptHistory.Clear();
        }
    }

    private void Flash(string text, bool error = false) {
        _status.ForeColor = error ? HudTheme.Negative : HudTheme.Positive;
        _status.Text = text;
        _statusReset.Stop();
        _statusReset.Start();
    }
}
