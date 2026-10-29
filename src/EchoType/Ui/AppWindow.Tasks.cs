using System.Globalization;
using EchoType.Hotkey;
using EchoType.Tasks;

namespace EchoType.Ui;

/// <summary>
/// The tasks side of the main window: Inbox (ideas first, then tasks), Tasks by date,
/// Projects, the notification center and the assistant/Telegram settings.
/// </summary>
internal sealed partial class AppWindow {

    private const string GlyphInbox = "";
    private const string GlyphTasks = "";
    private const string GlyphProjects = "";
    private const string GlyphBell = "";
    private const string GlyphSync = "";

    private sealed class TaskSection {
        public required HudSectionLabel Label { get; init; }
        public required HudCard Card { get; init; }
        public required HudItemList List { get; init; }
    }

    // Inbox
    private HudSettingRow _undoRow = null!;
    private HudButton _undoButton = null!;
    private TaskSection _inboxIdeas = null!;
    private TaskSection _inboxTasks = null!;
    // Tasks
    private TaskSection _overdue = null!;
    private TaskSection _today = null!;
    private TaskSection _upcoming = null!;
    private TaskSection _someday = null!;
    private TaskSection _done = null!;
    // Projects
    private HudItemList _projectList = null!;
    private TaskSection _projectIdeas = null!;
    private TaskSection _projectTasks = null!;
    private string? _selectedProject;
    // Notifications
    private HudItemList _activeNotices = null!;
    private TaskSection _noticeHistory = null!;
    // Settings
    private HudHotkeyRow _quickAddKey = null!;
    private HudHotkeyRow _taskVoiceKey = null!;
    private HudSettingRow _syncRow = null!;
    private HudButton _syncButton = null!;
    private HudSegmented _engineSeg = null!;
    private HudToggleRow _telegramToggle = null!;
    private HudToggleRow _telegramPollsToggle = null!;
    private ComboBox _reminderHour = null!;
    private ComboBox _chimeRepeat = null!;
    private HudSettingRow _zoneRow = null!;

    private static readonly int[] ChimeChoices = [0, 2, 5, 10, 15, 30, 60];

    private TasksHub Hub => _host.Tasks;

    private void BuildTaskPages() {
        // ── Inbox ─────────────────────────────────────────────
        var inbox = AddPage(GlyphInbox, Loc.T("Inbox"), Loc.T("Ideas and tasks that aren't in a project yet."));
        var capture = AddCard(inbox);
        var input = new HudInputRow(Loc.T("Write an idea and press Enter…"), Loc.T("Add"));
        input.Submitted += text => {
            var item = Hub.QuickAdd(text);
            if (item is { ProjectIds.Count: > 0 }) {
                StatusOverlay.ShowNotice(TaskAssistant.Line(item, Hub.Store.Projects()), OverlayKind.Success, Loc.T("Added"));
            }
        };
        AddRowControl(capture, input, 60);
        var quickBar = MakeButton(Loc.T("Open quick-add bar"), HudButton.Kind.Ghost);
        quickBar.Click += (_, _) => _host.OpenQuickAdd();
        AddRow(capture, Loc.T("Quick add from any app"), Loc.T("Press the Quick add shortcut, type, Enter."), quickBar, 200);
        _undoButton = MakeButton(Loc.T("Undo"), HudButton.Kind.Ghost);
        _undoButton.Click += (_, _) => {
            string? undone = Hub.Undo();
            StatusOverlay.ShowNotice(undone == null ? Loc.T("Nothing to undo.") : Loc.F("Undone: {0}", undone));
        };
        _undoRow = AddRow(capture, Loc.T("Undo last change"), Loc.T("Nothing to undo."), _undoButton, 140);
        _inboxIdeas = AddSectionList(inbox, "💡 " + Loc.T("Ideas"), Loc.T("No ideas in the Inbox."));
        _inboxTasks = AddSectionList(inbox, "📌 " + Loc.T("Tasks"), Loc.T("No tasks in the Inbox."));

        // ── Tasks ─────────────────────────────────────────────
        var tasks = AddPage(GlyphTasks, Loc.T("Tasks"), Loc.T("Everything with a date, in Baghdad time."));
        var taskTools = AddCard(tasks);
        var newTask = MakeButton(Loc.T("New task…"), HudButton.Kind.Primary);
        newTask.Click += (_, _) => EditItem(null, ItemKind.Task);
        AddRow(taskTools, Loc.T("Add a task"), Loc.T("Or just say it with the Tasks voice shortcut."), newTask, 160);
        _overdue = AddSectionList(tasks, "⚠️ " + Loc.T("Overdue"), "");
        _today = AddSectionList(tasks, "📅 " + Loc.T("Today"), Loc.T("Nothing due today."));
        _upcoming = AddSectionList(tasks, "🗓 " + Loc.T("Upcoming"), "");
        _someday = AddSectionList(tasks, "📌 " + Loc.T("No date"), "");
        _done = AddSectionList(tasks, "✅ " + Loc.T("Recently completed"), "");

        // ── Projects ──────────────────────────────────────────
        var projects = AddPage(GlyphProjects, Loc.T("Projects"), Loc.T("Ideas that mention a project are filed under it automatically."));
        var projectTools = AddCard(projects);
        var newProject = MakeButton(Loc.T("New project…"), HudButton.Kind.Primary);
        newProject.Click += (_, _) => EditProject(null);
        AddRow(projectTools, Loc.T("Projects"), Loc.T("Give each project its name and nicknames (e.g. Joker, جوكر)."), newProject, 160);
        var link = MakeButton(Loc.T("Link"), HudButton.Kind.Ghost);
        link.Click += (_, _) => {
            int linked = Hub.LinkInboxToProjects();
            StatusOverlay.ShowNotice(Loc.F("Linked {0} Inbox items to their projects.", linked));
        };
        AddRow(projectTools, Loc.T("File Inbox items"), Loc.T("Move Inbox items that name a project into it."), link, 140);
        _projectList = new HudItemList { EmptyText = Loc.T("No projects yet.") };
        AddRowControl(projectTools, _projectList, 0);
        _projectIdeas = AddSectionList(projects, "💡 " + Loc.T("Ideas"), Loc.T("No ideas in this project."));
        _projectTasks = AddSectionList(projects, "📌 " + Loc.T("Tasks"), Loc.T("No tasks in this project."));

        // ── Notification center ───────────────────────────────
        var notices = AddPage(GlyphBell, Loc.T("Notifications"), Loc.T("Reminders stay here until you act on them."));
        AddSection(notices, Loc.T("Active"));
        var activeCard = AddCard(notices);
        _activeNotices = new HudItemList { EmptyText = Loc.T("No reminders waiting.") };
        AddRowControl(activeCard, _activeNotices, 0);
        _noticeHistory = AddSectionList(notices, Loc.T("History"), Loc.T("No reminders yet."));
        var clear = MakeButton(Loc.T("Clear"), HudButton.Kind.Ghost);
        clear.Click += (_, _) => Hub.Store.Change("clear history", c => c.ClearNoticeHistory());
        AddRow(_noticeHistory.Card, Loc.T("Clear history"), null, clear, 120);
    }

    private void BuildTaskSettingsPage() {
        var page = AddPage(GlyphSync, Loc.T("Tasks & sync"), Loc.T("Shortcuts, the assistant, Telegram and reminders."));
        AddSection(page, Loc.T("Shortcuts"));
        var keys = AddCard(page);
        _quickAddKey = AddHotkey(keys, Loc.T("Quick add shortcut"), AppShortcut.QuickAdd);
        _quickAddKey.MultiKey = true;
        _taskVoiceKey = AddHotkey(keys, Loc.T("Tasks voice shortcut (hold and speak)"), AppShortcut.TaskVoice);
        _taskVoiceKey.MultiKey = true;

        AddSection(page, Loc.T("Assistant"));
        var assistant = AddCard(page);
        _syncButton = MakeButton(Loc.T("Set up…"), HudButton.Kind.Ghost);
        _syncButton.Click += (_, _) => _host.EditTaskSync();
        _syncRow = AddRow(assistant, Loc.T("Gemini & Telegram"), "", _syncButton, 160);
        _engineSeg = new HudSegmented { Items = [Loc.T("Auto"), "Gemini", Loc.T("Web model")] };
        _engineSeg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                _host.Settings.TaskAssistantEngine = _engineSeg.SelectedIndex switch { 1 => "gemini", 2 => "web", _ => "auto" };
                _host.SaveTaskSettings();
            }
        };
        AddRow(assistant, Loc.T("Understands spoken commands with"),
            Loc.T("Auto: Gemini when a key is set, else the cleaning model."), _engineSeg, 300);

        AddSection(page, "Telegram");
        var telegram = AddCard(page);
        _telegramToggle = AddToggle(telegram, Loc.T("Sync with Telegram"),
            Loc.T("Texts and voice notes you send the bot become ideas and tasks here."),
            toggle => {
                _host.Settings.TelegramEnabled = toggle.Checked;
                _host.SaveTaskSettings();
            });
        _telegramPollsToggle = AddToggle(telegram, Loc.T("Send reminders to Telegram as polls"),
            Loc.T("Vote Done, Snooze, Stop or Delete on the phone; the PC follows."),
            toggle => {
                _host.Settings.TelegramReminders = toggle.Checked;
                _host.SaveTaskSettings();
            });

        AddSection(page, Loc.T("Reminders"));
        var reminders = AddCard(page);
        _reminderHour = MakeCombo(Enumerable.Range(0, 24).Select(h => h.ToString("00", CultureInfo.InvariantCulture) + ":00"),
            index => {
                _host.Settings.DefaultReminderHour = index;
                _host.SaveTaskSettings();
            });
        AddRow(reminders, Loc.T("Reminder time for a day without a time"), null, _reminderHour, 140, heightDip: 0);
        _chimeRepeat = MakeCombo(ChimeChoices.Select(m => m == 0 ? Loc.T("Once") : Loc.F("Every {0} min", m)),
            index => {
                _host.Settings.ReminderRepeatMinutes = ChimeChoices[Math.Clamp(index, 0, ChimeChoices.Length - 1)];
                _host.SaveTaskSettings();
            });
        AddRow(reminders, Loc.T("Chime again while unanswered"), null, _chimeRepeat, 160, heightDip: 0);
        var zone = MakeButton(Loc.T("Baghdad"), HudButton.Kind.Ghost);
        zone.Enabled = false;
        _zoneRow = AddRow(reminders, Loc.T("Time zone"), "", zone, 140);
    }

    private TaskSection AddSectionList(Page page, string title, string empty) {
        var label = new HudSectionLabel { Text = title };
        page.Body.Controls.Add(label);
        page.Blocks.Add(label);
        var card = AddCard(page);
        var list = new HudItemList { EmptyText = empty };
        AddRowControl(card, list, 0);
        return new TaskSection { Label = label, Card = card, List = list };
    }

    private ComboBox MakeCombo(IEnumerable<string> items, Action<int> picked) {
        var combo = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Standard,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = HudTheme.BodyText,
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        combo.Items.AddRange(items.Cast<object>().ToArray());
        combo.SelectedIndexChanged += (_, _) => {
            if (!_syncing && combo.SelectedIndex >= 0) {
                picked(combo.SelectedIndex);
            }
        };
        return combo;
    }

    private void SetSectionVisible(TaskSection section, bool visible) {
        if (visible) {
            _hiddenBlocks.Remove(section.Label);
            _hiddenBlocks.Remove(section.Card);
        } else {
            _hiddenBlocks.Add(section.Label);
            _hiddenBlocks.Add(section.Card);
        }
    }

    // ── Sync ──────────────────────────────────────────────────

    private void SyncTaskSettings() {
        var s = _host.Settings;
        if (_capturing != _quickAddKey) {
            _quickAddKey.Value = HotkeyNames.For(s.QuickAddChord);
        }
        if (_capturing != _taskVoiceKey) {
            _taskVoiceKey.Value = HotkeyNames.For(s.TaskVoiceChord);
        }
        _quickAddKey.Invalidate();
        _taskVoiceKey.Invalidate();
        bool gemini = GeminiClient.ResolveKey(s).Length > 0;
        bool telegram = s.TelegramBotToken.Trim().Length > 0;
        bool chat = TelegramBot.TryParseChatId(s.TelegramChatId, out _);
        _syncRow.Description = (gemini ? "Gemini ✓" : Loc.T("Gemini: no key")) + " · "
            + (!telegram ? Loc.T("Telegram: not set up")
                : !chat ? Loc.T("Telegram: chat id missing")
                : Hub.TelegramRunning ? Loc.T("Telegram: connected") : Loc.T("Telegram: off"));
        _syncRow.Invalidate();
        _engineSeg.SetSilent((s.TaskAssistantEngine ?? "auto").ToLowerInvariant() switch { "gemini" => 1, "web" => 2, _ => 0 });
        _telegramToggle.SetSilent(s.TelegramEnabled);
        _telegramPollsToggle.SetSilent(s.TelegramReminders);
        _reminderHour.SelectedIndex = Math.Clamp(s.DefaultReminderHour, 0, 23);
        int chime = Array.IndexOf(ChimeChoices, s.ReminderRepeatMinutes);
        _chimeRepeat.SelectedIndex = chime >= 0 ? chime : 2;
        _zoneRow.Description = TaskTime.Zone.DisplayName + " · " + TaskTime.LocalNow.ToString("HH:mm", CultureInfo.InvariantCulture);
        _zoneRow.Invalidate();
    }

    private void ResetTaskKeyRows() {
        if (_quickAddKey == null) {
            return;
        }
        _quickAddKey.CapturePreview = null;
        _quickAddKey.Value = HotkeyNames.For(_host.Settings.QuickAddChord);
        _quickAddKey.Invalidate();
        _taskVoiceKey.CapturePreview = null;
        _taskVoiceKey.Value = HotkeyNames.For(_host.Settings.TaskVoiceChord);
        _taskVoiceKey.Invalidate();
    }

    /// <summary>Reloads every list from the store.</summary>
    private void SyncTasks() {
        var items = Hub.Store.Items();
        var projects = Hub.Store.Projects();
        var names = projects.ToDictionary(p => p.Id, p => p.Name);
        var open = items.Where(i => i.IsOpen).ToList();
        var nowLocal = TaskTime.LocalNow;
        var today = nowLocal.Date;

        // Inbox: no project. Ideas first, then tasks.
        var inbox = open.Where(i => i.ProjectIds.Count == 0).ToList();
        _inboxIdeas.List.Reload(inbox.Where(i => i.Kind == ItemKind.Idea)
            .OrderByDescending(i => i.CreatedAt).Select(i => ItemRow(i, names)).ToList());
        _inboxTasks.List.Reload(inbox.Where(i => i.Kind == ItemKind.Task)
            .OrderBy(i => i.Due ?? DateTimeOffset.MaxValue).ThenByDescending(i => i.CreatedAt)
            .Select(i => ItemRow(i, names)).ToList());
        string? undo = Hub.Store.LastUndoDescription;
        _undoRow.Description = undo ?? Loc.T("Nothing to undo.");
        _undoButton.Enabled = undo != null;
        _undoRow.Invalidate();

        // Tasks by date.
        var dated = open.Where(i => i.Kind == ItemKind.Task && i.Due != null).OrderBy(i => i.Due).ToList();
        // Earlier today stays under Today (marked overdue); earlier days are Overdue.
        var overdue = dated.Where(i => TaskTime.ToLocal(i.Due!.Value).Date < today).ToList();
        var todayList = dated.Where(i => TaskTime.ToLocal(i.Due!.Value).Date == today).ToList();
        var upcoming = dated.Where(i => TaskTime.ToLocal(i.Due!.Value).Date > today).ToList();
        var someday = open.Where(i => i.Kind == ItemKind.Task && i.Due == null).OrderByDescending(i => i.CreatedAt).ToList();
        var done = items.Where(i => i.Completed).OrderByDescending(i => i.CompletedAt).Take(15).ToList();
        _overdue.List.Reload(overdue.Select(i => ItemRow(i, names)).ToList());
        _today.List.Reload(todayList.Select(i => ItemRow(i, names)).ToList());
        _upcoming.List.Reload(upcoming.Take(60).Select(i => ItemRow(i, names)).ToList());
        _someday.List.Reload(someday.Select(i => ItemRow(i, names)).ToList());
        _done.List.Reload(done.Select(i => ItemRow(i, names)).ToList());
        SetSectionVisible(_overdue, overdue.Count > 0);
        SetSectionVisible(_upcoming, upcoming.Count > 0);
        SetSectionVisible(_someday, someday.Count > 0);
        SetSectionVisible(_done, done.Count > 0);

        // Projects.
        if (_selectedProject != null && !names.ContainsKey(_selectedProject)) {
            _selectedProject = null;
        }
        _selectedProject ??= projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault()?.Id;
        _projectList.Reload(projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => {
            var mine = open.Where(i => i.ProjectIds.Contains(p.Id)).ToList();
            string subtitle = Loc.F("{0} ideas, {1} tasks", mine.Count(i => i.Kind == ItemKind.Idea), mine.Count(i => i.Kind == ItemKind.Task));
            if (p.Keywords.Count > 0) {
                subtitle += " · " + string.Join(", ", p.Keywords);
            }
            string id = p.Id;
            return new ListRow {
                Glyph = p.Id == _selectedProject ? "📂" : "📁",
                Title = p.Name,
                Subtitle = subtitle,
                SubtitleColor = p.Id == _selectedProject ? HudTheme.Info : null,
                Clicked = () => {
                    _selectedProject = id;
                    SyncTasks();
                    Relayout();
                },
                Actions = [
                    new RowAction(Loc.T("Edit"), () => EditProject(id)),
                    new RowAction(Loc.T("Delete"), () => DeleteProject(id), Danger: true),
                ],
            };
        }).ToList());
        var selected = _selectedProject;
        var inProject = selected == null ? [] : open.Where(i => i.ProjectIds.Contains(selected)).ToList();
        string projectName = selected != null && names.TryGetValue(selected, out var n) ? n : "";
        _projectIdeas.Label.Text = "💡 " + Loc.T("Ideas") + (projectName.Length > 0 ? " · " + projectName : "");
        _projectTasks.Label.Text = "📌 " + Loc.T("Tasks") + (projectName.Length > 0 ? " · " + projectName : "");
        _projectIdeas.Label.Invalidate();
        _projectTasks.Label.Invalidate();
        _projectIdeas.List.Reload(inProject.Where(i => i.Kind == ItemKind.Idea).OrderByDescending(i => i.CreatedAt)
            .Select(i => ItemRow(i, names, hideProject: selected)).ToList());
        _projectTasks.List.Reload(inProject.Where(i => i.Kind == ItemKind.Task)
            .OrderBy(i => i.Due ?? DateTimeOffset.MaxValue).Select(i => ItemRow(i, names, hideProject: selected)).ToList());
        SetSectionVisible(_projectIdeas, selected != null);
        SetSectionVisible(_projectTasks, selected != null);

        // Notification center.
        var notices = Hub.Store.Notices();
        var active = notices.Where(x => x.IsOpen).OrderByDescending(x => x.FiredAt).ToList();
        _activeNotices.Reload(active.Select(NoticeRow).ToList());
        var history = notices.Where(x => !x.IsOpen).OrderByDescending(x => x.ResolvedAt ?? x.FiredAt).Take(40).ToList();
        _noticeHistory.List.Reload(history.Select(NoticeRow).ToList());
        _pages[NotificationsPage].Nav.Text = active.Count > 0
            ? Loc.T("Notifications") + " (" + active.Count.ToString(CultureInfo.CurrentCulture) + ")"
            : Loc.T("Notifications");
        _pages[NotificationsPage].Nav.Invalidate();
        _pages[InboxPage].Nav.Text = inbox.Count > 0
            ? Loc.T("Inbox") + " (" + inbox.Count.ToString(CultureInfo.CurrentCulture) + ")"
            : Loc.T("Inbox");
        _pages[InboxPage].Nav.Invalidate();
    }

    private ListRow ItemRow(TaskItem item, Dictionary<string, string> projectNames, string? hideProject = null) {
        var parts = new List<string>();
        bool overdue = false;
        if (item.Due is { } due) {
            overdue = item.IsOpen && due < TaskTime.UtcNow;
            parts.Add((overdue ? "⚠ " : "") + TaskTime.Describe(due, item.HasTime));
        }
        if (item.Recurrence != null) {
            parts.Add("🔁 " + RecurrenceMath.Describe(item.Recurrence));
        }
        var projects = item.ProjectIds.Where(id => id != hideProject && projectNames.ContainsKey(id)).Select(id => projectNames[id]).ToList();
        if (projects.Count > 0) {
            parts.Add("📁 " + string.Join(", ", projects));
        }
        if (item.Source == "telegram") {
            parts.Add("📱");
        }
        if (item.Notes.Length > 0) {
            parts.Add(item.Notes.Replace('\n', ' '));
        }
        string id = item.Id;
        var actions = new List<RowAction>();
        if (item.Kind == ItemKind.Idea && item.IsOpen) {
            actions.Add(new RowAction(Loc.T("Make task"), () => Hub.SetKind(id, ItemKind.Task)));
        }
        actions.Add(new RowAction(Loc.T("Delete"), () => Hub.Delete(id), Danger: true));
        return new ListRow {
            Check = item.Kind == ItemKind.Task,
            Checked = item.Completed,
            CheckClicked = () => {
                if (item.Completed) {
                    Hub.Reopen(id);
                } else {
                    Hub.Complete(id);
                }
            },
            Glyph = item.Kind == ItemKind.Idea ? "💡" : "",
            Title = item.Title,
            Subtitle = string.Join(" · ", parts),
            SubtitleColor = overdue ? HudTheme.Negative : null,
            Clicked = () => EditItem(id, item.Kind),
            Actions = actions,
        };
    }

    private ListRow NoticeRow(Notice notice) {
        string id = notice.Id;
        string when = TaskTime.Describe(notice.Occurrence, true);
        string state = notice.State switch {
            NoticeState.Active => notice.Late ? Loc.T("late") : Loc.T("waiting"),
            NoticeState.Snoozed => Loc.F("snoozed until {0}", TaskTime.Describe(notice.SnoozeUntil ?? notice.Occurrence, true)),
            NoticeState.Done => Loc.T("done"),
            NoticeState.Deleted => Loc.T("deleted"),
            _ => Loc.T("stopped"),
        };
        if (notice.ResolvedFrom == "telegram") {
            state += " · 📱";
        }
        var row = new ListRow {
            Glyph = notice.State switch {
                NoticeState.Active => "⏰",
                NoticeState.Snoozed => "💤",
                NoticeState.Done => "✅",
                NoticeState.Deleted => "🗑",
                _ => "🔕",
            },
            Title = notice.Title,
            Subtitle = when + " · " + state,
            SubtitleColor = notice.State == NoticeState.Active && notice.Late ? HudTheme.Caution : null,
            Muted = !notice.IsOpen,
            ActionsOnHover = !notice.IsOpen,
            Clicked = Hub.Store.Find(notice.TaskId) != null ? () => EditItem(notice.TaskId, ItemKind.Task) : null,
            Actions = notice.IsOpen
                ? [
                    new RowAction(Loc.T("Done"), () => Hub.ResolveNotice(id, NoticeAction.Done, "app"), Primary: true),
                    new RowAction(Loc.T("1 hour"), () => Hub.ResolveNotice(id, NoticeAction.Snooze60, "app")),
                    new RowAction(Loc.T("Stop"), () => Hub.ResolveNotice(id, NoticeAction.Stop, "app")),
                ]
                : [],
        };
        return row;
    }

    // ── Editing ───────────────────────────────────────────────

    private void EditItem(string? id, ItemKind kind) {
        var existing = id == null ? null : Hub.Store.Find(id);
        bool isNew = existing == null;
        var item = existing ?? new TaskItem { Kind = kind, Source = "app" };
        if (isNew && _page == 2 && _selectedProject != null) {
            item.ProjectIds.Add(_selectedProject);
        }
        using var form = new TaskEditorForm(item, Hub.Store.Projects(), Hub.DefaultHour, isNew);
        if (form.ShowDialog(this) != DialogResult.OK) {
            return;
        }
        if (form.DeleteRequested) {
            Hub.Delete(item.Id);
        } else {
            Hub.Save(form.Result);
        }
    }

    private void EditProject(string? id) {
        var project = id == null ? null : Hub.Store.FindProject(id);
        using var form = new ProjectEditorForm(project);
        if (form.ShowDialog(this) != DialogResult.OK) {
            return;
        }
        if (project == null) {
            _selectedProject = Hub.AddProject(form.ProjectName, form.Keywords).Id;
            int linked = Hub.LinkInboxToProjects();
            if (linked > 0) {
                StatusOverlay.ShowNotice(Loc.F("Linked {0} Inbox items to their projects.", linked));
            }
        } else {
            Hub.SaveProject(project.Id, form.ProjectName, form.Keywords);
        }
    }

    private void DeleteProject(string id) {
        var project = Hub.Store.FindProject(id);
        if (project == null) {
            return;
        }
        var answer = MessageBox.Show(this,
            Loc.F("Delete the project \"{0}\"? Its ideas and tasks stay (in the Inbox if they have no other project).", project.Name),
            "EchoType", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK) {
            Hub.DeleteProject(id);
        }
    }
}
