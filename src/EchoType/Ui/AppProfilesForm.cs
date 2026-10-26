using System.Diagnostics;
using EchoType.Translation;

namespace EchoType.Ui;

/// <summary>
/// Edit the per-app profiles: one row per group of apps (process names) with optional
/// overrides for Auto Enter, Auto Clean, the cleaning prompt and the Translate language.
/// </summary>
internal sealed class AppProfilesForm : Form {

    private const string Global = "";
    private const string On = "on";
    private const string Off = "off";

    private const int AppsCol = 0;
    private const int EnterCol = 1;
    private const int CleanCol = 2;
    private const int PromptCol = 3;
    private const int TranslateCol = 4;

    private const string IraqiChatPrompt =
        "Clean up this dictated chat message. It is in Iraqi Arabic dialect and may mix in English words: "
        + "keep the Iraqi dialect words and spelling exactly as spoken — do not convert them to Modern Standard Arabic. "
        + "Remove filler words, repeated words and false starts; if I corrected myself keep only the final version; "
        + "add light punctuation. Keep it short and casual like a chat message. "
        + "Reply with ONLY the cleaned text, no quotes, no explanation.";

    private sealed record Choice(string Value, string Label);

    private readonly DataGridView _grid;
    private readonly string _globalPrompt;
    private readonly string _modelName;

    public List<AppProfile> Result { get; private set; }

    public AppProfilesForm(IEnumerable<AppProfile> profiles, string globalPrompt, string modelName) {
        Result = profiles.Select(Clone).ToList();
        _globalPrompt = globalPrompt;
        _modelName = modelName;

        Text = Loc.T("Per-app profiles");
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        ClientSize = new Size(900, 460);
        MinimumSize = new Size(640, 340);
        Padding = new Padding(14);

        var title = new Label {
            Dock = DockStyle.Top,
            AutoSize = true,
            Text = Loc.T("Override Auto Enter, Auto Clean and the Translate language for the app you start dictating in"),
            Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(0, 0, 0, 8),
        };
        var hint = new Label {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = Loc.T("Apps: process names, comma separated (e.g. Telegram, WhatsApp, Code, Obsidian) — use Add running app to pick one. "
                + "\"Global\" keeps the main setting. Double-click a prompt to edit it; an empty prompt uses the global one. "
                + "The first row that lists the app wins."),
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
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        Choice[] toggles = [new(Global, Loc.T("Global")), new(On, Loc.T("On")), new(Off, Loc.T("Off"))];
        Choice[] languages = [
            new(Global, Loc.T("Global")),
            .. GoogleTranslation.Languages.Select(l => new Choice(l.Code, l.Name)),
        ];
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "apps", HeaderText = Loc.T("Apps"), FillWeight = 24 });
        _grid.Columns.Add(MakeChoiceColumn("enter", Loc.T("Auto Enter"), toggles, 12));
        _grid.Columns.Add(MakeChoiceColumn("clean", Loc.T("Auto Clean"), toggles, 12));
        _grid.Columns.Add(new DataGridViewTextBoxColumn {
            Name = "prompt", HeaderText = Loc.T("Cleaning prompt"), FillWeight = 36, ReadOnly = true,
        });
        _grid.Columns.Add(MakeChoiceColumn("translate", Loc.T("Translate to"), languages, 16));
        _grid.DefaultValuesNeeded += (_, e) => {
            e.Row.Cells[EnterCol].Value = Global;
            e.Row.Cells[CleanCol].Value = Global;
            e.Row.Cells[PromptCol].Value = "";
            e.Row.Cells[TranslateCol].Value = Global;
        };
        _grid.CellFormatting += OnCellFormatting;
        _grid.CellDoubleClick += (_, e) => {
            if (e.RowIndex >= 0 && e.ColumnIndex == PromptCol) {
                EditPrompt(_grid.Rows[e.RowIndex]);
            }
        };
        _grid.DataError += (_, e) => e.ThrowException = false;
        FillGrid(Result);

        var save = new Button { Text = Loc.T("Save"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var remove = new Button { Text = Loc.T("Remove selected"), AutoSize = true };
        var addApp = new Button { Text = Loc.T("Add running app ▾"), AutoSize = true };
        var editPrompt = new Button { Text = Loc.T("Edit prompt…"), AutoSize = true };
        var examples = new Button { Text = Loc.T("Add examples"), AutoSize = true };
        save.Click += (_, _) => Save();
        remove.Click += (_, _) => RemoveSelected();
        addApp.Click += (_, _) => ShowRunningApps(addApp);
        editPrompt.Click += (_, _) => {
            if (_grid.CurrentRow is { } row) {
                EditPrompt(row);
            }
        };
        examples.Click += (_, _) => AddExamples();
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
        buttons.Controls.Add(editPrompt);
        buttons.Controls.Add(addApp);
        buttons.Controls.Add(examples);

        Controls.Add(_grid);
        Controls.Add(title);
        Controls.Add(hint);
        Controls.Add(buttons);
        Loc.ApplyTo(this);
    }

    private static DataGridViewComboBoxColumn MakeChoiceColumn(string name, string header, Choice[] choices, float weight) =>
        new() {
            Name = name,
            HeaderText = header,
            FillWeight = weight,
            DataSource = choices,
            ValueMember = nameof(Choice.Value),
            DisplayMember = nameof(Choice.Label),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };

    private static AppProfile Clone(AppProfile p) => new() {
        Apps = [.. p.Apps],
        PressEnter = p.PressEnter,
        AutoClean = p.AutoClean,
        CleanPrompt = p.CleanPrompt,
        TranslateTo = p.TranslateTo,
    };

    private static string ToChoice(bool? value) => value switch {
        true => On,
        false => Off,
        null => Global,
    };

    private static bool? FromChoice(object? value) => (value as string) switch {
        On => true,
        Off => false,
        _ => null,
    };

    private void FillGrid(IEnumerable<AppProfile> profiles) {
        _grid.Rows.Clear();
        foreach (var p in profiles) {
            AddRow(p);
        }
    }

    private void AddRow(AppProfile p) {
        string translate = GoogleTranslation.Languages.Any(l => l.Code == p.TranslateTo) ? p.TranslateTo : Global;
        _grid.Rows.Add(p.DisplayName, ToChoice(p.PressEnter), ToChoice(p.AutoClean), p.CleanPrompt, translate);
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _grid.Select();
        int last = _grid.Rows.Count - 1;
        if (last >= 0) {
            _grid.CurrentCell = _grid.Rows[last].Cells[AppsCol];
        }
    }

    /// <summary>The prompt cell shows one line, or "(global prompt)" when empty.</summary>
    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e) {
        if (e.ColumnIndex != PromptCol || e.RowIndex < 0 || _grid.Rows[e.RowIndex].IsNewRow) {
            return;
        }
        string prompt = e.Value as string ?? "";
        if (prompt.Length == 0) {
            e.Value = Loc.T("(global prompt)");
            e.CellStyle!.ForeColor = SystemColors.GrayText;
        } else {
            e.Value = prompt.Replace("\r", " ").Replace("\n", " ");
        }
        e.FormattingApplied = true;
    }

    private void EditPrompt(DataGridViewRow row) {
        if (row.IsNewRow) {
            _grid.EndEdit();
            row = _grid.Rows[_grid.Rows.Add("", Global, Global, "", Global)];
        }
        string current = row.Cells[PromptCol].Value as string ?? "";
        using var form = new CleanPromptForm(current.Length > 0 ? current : _globalPrompt, _modelName);
        if (form.ShowDialog(this) != DialogResult.OK) {
            return;
        }
        // Saving the global prompt unchanged keeps this row following later global edits.
        row.Cells[PromptCol].Value = form.Result == _globalPrompt.Trim() ? "" : form.Result;
        if (row.Cells[PromptCol].Value is string { Length: > 0 } && FromChoice(row.Cells[CleanCol].Value) == null) {
            row.Cells[CleanCol].Value = On;
        }
    }

    private void ShowRunningApps(Control anchor) {
        var menu = new ContextMenuStrip();
        foreach (string name in RunningAppNames()) {
            menu.Items.Add(name, null, (_, _) => AddApp(name));
        }
        if (menu.Items.Count == 0) {
            menu.Items.Add(new ToolStripMenuItem(Loc.T("No apps with windows found")) { Enabled = false });
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private static List<string> RunningAppNames() {
        int self = Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses()) {
            using (p) {
                try {
                    if (p.Id != self && p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0
                        && p.ProcessName is not ("explorer" or "ApplicationFrameHost" or "TextInputHost")) {
                        names.Add(p.ProcessName);
                    }
                } catch {
                    // Exited or access denied — skip.
                }
            }
        }
        return [.. names];
    }

    /// <summary>Adds the app to the selected row's list, or as a new row.</summary>
    private void AddApp(string name) {
        _grid.EndEdit();
        var row = _grid.CurrentRow;
        if (row is { IsNewRow: false }) {
            var apps = SplitApps(row.Cells[AppsCol].Value as string);
            if (!apps.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                apps.Add(name);
                row.Cells[AppsCol].Value = string.Join(", ", apps);
            }
            return;
        }
        int index = _grid.Rows.Add(name, Global, Global, "", Global);
        _grid.CurrentCell = _grid.Rows[index].Cells[EnterCol];
    }

    private void AddExamples() {
        _grid.EndEdit();
        AddRow(new AppProfile {
            Apps = ["Telegram", "WhatsApp"],
            PressEnter = true,
            AutoClean = true,
            CleanPrompt = IraqiChatPrompt,
        });
        AddRow(new AppProfile {
            Apps = ["Code", "Obsidian"],
            PressEnter = false,
            AutoClean = false,
        });
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

    private static List<string> SplitApps(string? text) =>
        (text ?? "").Split(new[] { ',', '،', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(AppProfile.NormalizeApp)
            .Where(a => a.Length > 0)
            .ToList();

    private List<AppProfile> ReadGrid() {
        var profiles = new List<AppProfile>();
        foreach (DataGridViewRow row in _grid.Rows) {
            if (row.IsNewRow) {
                continue;
            }
            var profile = new AppProfile {
                Apps = SplitApps(row.Cells[AppsCol].Value as string),
                PressEnter = FromChoice(row.Cells[EnterCol].Value),
                AutoClean = FromChoice(row.Cells[CleanCol].Value),
                CleanPrompt = row.Cells[PromptCol].Value as string ?? "",
                TranslateTo = row.Cells[TranslateCol].Value as string ?? "",
            };
            profile.Normalize();
            if (profile.Apps.Count > 0) {
                profiles.Add(profile);
            }
        }
        return profiles;
    }

    private void Save() {
        _grid.EndEdit();
        Result = ReadGrid();
        DialogResult = DialogResult.OK;
        Close();
    }
}
