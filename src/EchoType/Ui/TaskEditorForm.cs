using EchoType.Tasks;

namespace EchoType.Ui;

/// <summary>
/// Edit one idea or task: title, idea/task, projects (any number), date and time, repeat and
/// notes. Works on a copy; <see cref="Result"/> holds the edited item after OK.
/// </summary>
internal sealed class TaskEditorForm : Form {

    private readonly TaskItem _item;
    private readonly TextBox _title;
    private readonly RadioButton _idea;
    private readonly RadioButton _task;
    private readonly CheckedListBox _projects;
    private readonly CheckBox _hasDate;
    private readonly DateTimePicker _date;
    private readonly CheckBox _hasTime;
    private readonly DateTimePicker _time;
    private readonly ComboBox _repeat;
    private readonly NumericUpDown _interval;
    private readonly FlowLayoutPanel _days;
    private readonly CheckBox _done;
    private readonly TextBox _notes;
    private readonly List<Project> _projectList;
    private readonly int _defaultHour;

    public TaskItem Result => _item;

    /// <summary>True when the user pressed Delete.</summary>
    public bool DeleteRequested { get; private set; }

    private static readonly DayOfWeek[] Week = [
        DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
        DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
    ];

    public TaskEditorForm(TaskItem item, IReadOnlyList<Project> projects, int defaultHour, bool isNew) {
        _item = TaskStore.Clone(item);
        _projectList = [.. projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        _defaultHour = defaultHour;

        Text = isNew ? Loc.T("New item") : (item.Kind == ItemKind.Task ? Loc.T("Edit task") : Loc.T("Edit idea"));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        _title = new TextBox { Dock = DockStyle.Fill, Text = _item.Title };
        if (ArabicFont.HasArabic(_item.Title) && ArabicFont.Create(10f) is { } arabic) {
            _title.Font = arabic;
        }
        AddRow(layout, Loc.T("Title"), _title);

        _idea = new RadioButton { Text = "💡 " + Loc.T("Idea"), AutoSize = true, Checked = _item.Kind == ItemKind.Idea };
        _task = new RadioButton { Text = "📌 " + Loc.T("Task"), AutoSize = true, Checked = _item.Kind == ItemKind.Task };
        var kind = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        kind.Controls.Add(_idea);
        kind.Controls.Add(_task);
        AddRow(layout, Loc.T("Type"), kind);

        _projects = new CheckedListBox {
            Dock = DockStyle.Fill,
            Height = 92,
            CheckOnClick = true,
            IntegralHeight = false,
        };
        foreach (var p in _projectList) {
            _projects.Items.Add(p.Name, _item.ProjectIds.Contains(p.Id));
        }
        AddRow(layout, Loc.T("Projects"), _projectList.Count == 0
            ? new Label { Text = Loc.T("No projects yet — add them on the Projects page."), AutoSize = true, ForeColor = SystemColors.GrayText }
            : _projects);

        var local = _item.Due is { } due ? TaskTime.ToLocal(due) : TaskTime.LocalNow.Date.AddDays(1).AddHours(defaultHour);
        _hasDate = new CheckBox { Text = Loc.T("Date"), AutoSize = true, Checked = _item.Due != null };
        _date = new DateTimePicker { Format = DateTimePickerFormat.Long, Value = local.Date, Width = 220 };
        _hasTime = new CheckBox { Text = Loc.T("Time"), AutoSize = true, Checked = _item.Due == null || _item.HasTime };
        _time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80, Value = local };
        var when = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        when.Controls.Add(_hasDate);
        when.Controls.Add(_date);
        when.Controls.Add(_hasTime);
        when.Controls.Add(_time);
        AddRow(layout, Loc.T("When"), when);
        layout.Controls.Add(new Label {
            Text = Loc.F("Times are {0}. A day without a time reminds at {1:00}:00.", TaskTime.Zone.DisplayName, defaultHour),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(420, 0),
        }, 1, layout.RowCount++);

        _repeat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _repeat.Items.AddRange([Loc.T("Doesn't repeat"), Loc.T("Daily"), Loc.T("Weekly"), Loc.T("Monthly"), Loc.T("Yearly")]);
        _interval = new NumericUpDown { Minimum = 1, Maximum = 365, Width = 60, Value = Math.Max(1, _item.Recurrence?.Interval ?? 1) };
        var every = new Label { Text = Loc.T("every"), AutoSize = true, Margin = new Padding(8, 6, 4, 0) };
        var repeatRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        repeatRow.Controls.Add(_repeat);
        repeatRow.Controls.Add(every);
        repeatRow.Controls.Add(_interval);
        AddRow(layout, Loc.T("Repeat"), repeatRow);

        _days = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(420, 0) };
        var names = System.Globalization.CultureInfo.CurrentUICulture.DateTimeFormat;
        foreach (var day in Week) {
            _days.Controls.Add(new CheckBox {
                Text = names.GetAbbreviatedDayName(day),
                Tag = day,
                AutoSize = true,
                Checked = _item.Recurrence?.Days.Contains(day) == true,
            });
        }
        AddRow(layout, Loc.T("On days"), _days);
        _repeat.SelectedIndex = _item.Recurrence == null ? 0 : _item.Recurrence.Frequency switch {
            RecurrenceFrequency.Daily => 1,
            RecurrenceFrequency.Weekly => 2,
            RecurrenceFrequency.Monthly => 3,
            _ => 4,
        };

        _done = new CheckBox { Text = Loc.T("Completed"), AutoSize = true, Checked = _item.Completed };
        AddRow(layout, "", _done);

        _notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 80, ScrollBars = ScrollBars.Vertical, Text = _item.Notes };
        AddRow(layout, Loc.T("Notes"), _notes);

        var save = new Button { Text = Loc.T("Save"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        var delete = new Button { Text = Loc.T("Delete"), AutoSize = true, Visible = !isNew, ForeColor = Color.Firebrick };
        save.Click += (_, _) => Save();
        delete.Click += (_, _) => {
            DeleteRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        buttons.Controls.Add(delete);
        layout.Controls.Add(buttons, 0, layout.RowCount++);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);

        _hasDate.CheckedChanged += (_, _) => SyncEnabled();
        _hasTime.CheckedChanged += (_, _) => SyncEnabled();
        _repeat.SelectedIndexChanged += (_, _) => SyncEnabled();
        SyncEnabled();
        Loc.ApplyTo(this);
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control control) {
        int row = layout.RowCount++;
        layout.Controls.Add(new Label {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(3, 7, 10, 3),
        }, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private void SyncEnabled() {
        _date.Enabled = _hasDate.Checked;
        _hasTime.Enabled = _hasDate.Checked;
        _time.Enabled = _hasDate.Checked && _hasTime.Checked;
        bool repeats = _repeat.SelectedIndex > 0;
        _interval.Enabled = repeats;
        _days.Enabled = _repeat.SelectedIndex == 2;
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        _title.Select();
        _title.SelectionStart = _title.TextLength;
    }

    private void Save() {
        string title = _title.Text.Trim();
        if (title.Length == 0) {
            _title.Select();
            return;
        }
        _item.Title = title;
        _item.Notes = _notes.Text.Trim();
        _item.Kind = _task.Checked ? ItemKind.Task : ItemKind.Idea;
        _item.ProjectIds = [];
        for (int i = 0; i < _projects.Items.Count; i++) {
            if (_projects.GetItemChecked(i)) {
                _item.ProjectIds.Add(_projectList[i].Id);
            }
        }

        Recurrence? rule = null;
        if (_repeat.SelectedIndex > 0) {
            rule = new Recurrence {
                Frequency = _repeat.SelectedIndex switch {
                    1 => RecurrenceFrequency.Daily,
                    2 => RecurrenceFrequency.Weekly,
                    3 => RecurrenceFrequency.Monthly,
                    _ => RecurrenceFrequency.Yearly,
                },
                Interval = (int)_interval.Value,
                Until = _item.Recurrence?.Until,
            };
            if (rule.Frequency == RecurrenceFrequency.Weekly) {
                foreach (CheckBox box in _days.Controls) {
                    if (box.Checked && box.Tag is DayOfWeek day) {
                        rule.Days.Add(day);
                    }
                }
            }
        }
        _item.Recurrence = rule;

        if (_hasDate.Checked || rule != null) {
            bool hasTime = _hasDate.Checked && _hasTime.Checked;
            var day = _hasDate.Checked ? _date.Value.Date : TaskTime.LocalNow.Date;
            var local = day + (hasTime ? _time.Value.TimeOfDay : TimeSpan.FromHours(_defaultHour));
            if (rule != null) {
                local = RecurrenceMath.FirstOnOrAfter(rule, local);
            }
            var newDue = TaskTime.FromLocal(local);
            // Keep the exact stored instant when nothing visible changed (seconds, offsets).
            if (_item.Due is not { } old || TaskTime.ToLocal(old) != local || _item.HasTime != hasTime) {
                _item.Due = newDue;
            }
            _item.HasTime = hasTime;
            _item.Kind = ItemKind.Task;
        } else {
            _item.Due = null;
            _item.HasTime = false;
        }
        _item.Completed = _done.Checked;
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Name and extra keywords of a project.</summary>
internal sealed class ProjectEditorForm : Form {

    private readonly TextBox _name;
    private readonly TextBox _keywords;

    public string ProjectName => _name.Text.Trim();

    public List<string> Keywords => _keywords.Text
        .Split([',', '،', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    public ProjectEditorForm(Project? project) {
        Text = project == null ? Loc.T("New project") : Loc.T("Edit project");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Font;
        Font = SystemFonts.MessageBoxFont;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        _name = new TextBox { Dock = DockStyle.Fill, Text = project?.Name ?? "" };
        _keywords = new TextBox { Dock = DockStyle.Fill, Text = string.Join(", ", project?.Keywords ?? []) };
        layout.Controls.Add(new Label { Text = Loc.T("Name"), AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(new Label { Text = Loc.T("Keywords"), AutoSize = true, Margin = new Padding(3, 7, 10, 3) }, 0, 1);
        layout.Controls.Add(_keywords, 1, 1);
        var hint = new Label {
            Text = Loc.T("Comma separated. Other spellings and nicknames, e.g. Joker, جوكر, الجوكر. Any idea that mentions the name or a keyword is linked to the project automatically."),
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            ForeColor = SystemColors.GrayText,
        };
        layout.Controls.Add(hint, 1, 2);

        var save = new Button { Text = Loc.T("Save"), AutoSize = true };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) => {
            if (ProjectName.Length == 0) {
                _name.Select();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        AcceptButton = save;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        Loc.ApplyTo(this);
    }
}
