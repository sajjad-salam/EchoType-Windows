using System.Globalization;
using System.Text;
using EchoType.Ui;

namespace EchoType.Tasks;

/// <summary>
/// Ideas, tasks, reminders and Telegram sync in one place. Lives on the UI thread (the tray
/// app owns it); the Telegram poller calls in from a background thread and every UI effect is
/// marshalled back. Reminders: a timer finds tasks whose time has come (including ones missed
/// while EchoType was closed), shows a reminder that stays until you act on it, and sends it to
/// Telegram as a poll. Acting in either place resolves it in both.
/// </summary>
internal sealed class TasksHub : IDisposable {

    private const int MaxHistoryTurns = 10;
    private static readonly TimeSpan HistoryLifetime = TimeSpan.FromMinutes(30);
    /// <summary>A reminder that fires more than this after its time is marked late.</summary>
    private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    private readonly Settings _settings;
    private readonly Control _marshal;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, DateTimeOffset> _lastChime = [];
    private readonly List<AssistantTurn> _history = [];
    private readonly object _historyGate = new();
    private DateTimeOffset _lastTurnAt;
    private TelegramBot? _bot;
    private string _botKey = "";

    public TaskStore Store { get; }

    /// <summary>Raised on the UI thread after any change to items, projects or reminders.</summary>
    public event Action? Changed;

    /// <summary>Asks the tray app to show a short HUD notice (UI thread).</summary>
    public event Action<string, OverlayKind>? Notify;

    public TasksHub(Settings settings, Control marshal) {
        _settings = settings;
        _marshal = marshal;
        TaskTime.Configure(settings.TaskTimeZone);
        Store = new TaskStore();
        Store.Changed += () => OnUi(() => {
            Changed?.Invoke();
        });
        Store.NoticesClosed += notices => OnUi(() => OnNoticesClosed(notices));
        _timer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _timer.Tick += (_, _) => CheckReminders();
    }

    public void Start() {
        ApplyTelegramSettings();
        // Reminders still open from the last run come back on screen (not re-sent to Telegram).
        foreach (var notice in Store.Notices().Where(n => n.State == NoticeState.Active)) {
            ShowToast(notice);
        }
        _timer.Start();
        CheckReminders();
    }

    public void Dispose() {
        _timer.Stop();
        _timer.Dispose();
        _bot?.Dispose();
        _bot = null;
        ReminderToast.CloseAll();
    }

    private void OnUi(Action action) {
        if (_marshal.IsDisposed) {
            return;
        }
        if (_marshal.InvokeRequired) {
            try {
                _marshal.BeginInvoke(action);
            } catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) {
                // shutting down
            }
        } else {
            action();
        }
    }

    public int DefaultHour => Math.Clamp(_settings.DefaultReminderHour, 0, 23);

    public bool HasGemini => GeminiClient.ResolveKey(_settings).Length > 0;

    // ── Quick add & editing ───────────────────────────────────

    /// <summary>The quick-add bar: the text becomes an idea in the Inbox (and every project it names).</summary>
    public TaskItem? QuickAdd(string text, ItemKind kind = ItemKind.Idea) {
        var item = TaskAssistant.AddIdea(Store, text, "quick", kind);
        if (item != null) {
            Log.Write("tasks: quick add " + item.Kind + " (" + item.ProjectIds.Count + " projects)");
            RememberTurn("user", text);
            RememberTurn("assistant", TaskAssistant.Line(item, Store.Projects()) + " [id " + item.Id + "]");
        }
        return item;
    }

    public void Complete(string id, string from = "app") {
        var item = Store.Find(id);
        if (item == null) {
            return;
        }
        Store.Change(Loc.F("Complete \"{0}\"", TaskAssistant.Short(item.Title)), c => c.Complete(id, from));
        CheckReminders();
    }

    public void Reopen(string id) {
        var item = Store.Find(id);
        if (item != null) {
            Store.Change(Loc.F("Reopen \"{0}\"", TaskAssistant.Short(item.Title)), c => c.Reopen(id));
        }
    }

    public void Delete(string id) {
        var item = Store.Find(id);
        if (item != null) {
            Store.Change(Loc.F("Delete \"{0}\"", TaskAssistant.Short(item.Title)), c => c.Remove(id));
        }
    }

    /// <summary>Turns an idea into a task (or back).</summary>
    public void SetKind(string id, ItemKind kind) {
        var item = Store.Find(id);
        if (item != null && item.Kind != kind) {
            Store.Change(Loc.F("Edit \"{0}\"", TaskAssistant.Short(item.Title)), c => c.Edit(id, i => i.Kind = kind));
        }
    }

    /// <summary>Saves an edited copy from the editor dialog.</summary>
    public void Save(TaskItem edited) {
        bool isNew = Store.Find(edited.Id) == null;
        Store.Change(Loc.F(isNew ? "Add \"{0}\"" : "Edit \"{0}\"", TaskAssistant.Short(edited.Title)), c => {
            if (isNew) {
                c.Add(edited);
                return;
            }
            var before = c.Find(edited.Id)!;
            bool rescheduled = before.Due != edited.Due || (before.Recurrence == null) != (edited.Recurrence == null);
            bool completedNow = edited.Completed && !before.Completed;
            c.Edit(edited.Id, item => {
                item.Kind = edited.Kind;
                item.Title = edited.Title;
                item.Notes = edited.Notes;
                item.ProjectIds = [.. edited.ProjectIds];
                item.Due = edited.Due;
                item.HasTime = edited.HasTime;
                item.Recurrence = edited.Recurrence;
                item.Completed = edited.Completed;
                item.CompletedAt = edited.Completed ? item.CompletedAt ?? DateTimeOffset.UtcNow : null;
            });
            if (completedNow) {
                c.CloseNotices(edited.Id, NoticeState.Done, "app");
            } else if (rescheduled) {
                c.CloseNotices(edited.Id, NoticeState.Dismissed, "app");
            }
        });
        CheckReminders();
    }

    public Project AddProject(string name, IEnumerable<string> keywords) =>
        Store.Change(Loc.F("Add project \"{0}\"", name), c => c.AddProject(name, keywords));

    public void SaveProject(string id, string name, IEnumerable<string> keywords) =>
        Store.Change(Loc.F("Edit project \"{0}\"", name), c => c.EditProject(id, p => {
            p.Name = name.Trim();
            p.Keywords = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList();
        }));

    public void DeleteProject(string id) {
        var project = Store.FindProject(id);
        if (project != null) {
            Store.Change(Loc.F("Delete project \"{0}\"", project.Name), c => c.RemoveProject(id));
        }
    }

    /// <summary>Links every Inbox item that names a project (e.g. after adding the project).</summary>
    public int LinkInboxToProjects() =>
        Store.Change(Loc.T("Link Inbox items to projects"), c => {
            int linked = 0;
            foreach (var item in c.Items.Where(i => i.IsOpen && i.ProjectIds.Count == 0).ToList()) {
                var ids = ProjectMatcher.Match(item.Title + " " + item.Notes, c.Projects).Select(p => p.Id).ToList();
                if (ids.Count > 0) {
                    c.Edit(item.Id, i => i.ProjectIds = ids);
                    linked++;
                }
            }
            return linked;
        });

    public string? Undo() {
        string? undone = Store.Undo();
        if (undone != null) {
            Log.Write("tasks: undo \"" + undone + "\"");
            RememberTurn("assistant", "Undone: " + undone);
        }
        return undone;
    }

    // ── Assistant ─────────────────────────────────────────────

    /// <summary>
    /// Runs one message through the assistant. <paramref name="webModel"/> answers a prompt with
    /// the cleaning model's web window (used for spoken commands when there is no Gemini key, or
    /// as a second chance when Gemini fails). Without any model the text is saved as an idea.
    /// </summary>
    public async Task<AssistantResult> RunAssistantAsync(string message, string source,
        Func<string, Task<string>>? webModel = null, CancellationToken cancel = default) {
        message = message.Trim();
        if (message.Length == 0) {
            return new AssistantResult();
        }
        string engine = (_settings.TaskAssistantEngine ?? "auto").Trim().ToLowerInvariant();
        var gemini = engine == "web" ? null : GeminiClient.FromSettings(_settings);
        var history = HistorySnapshot();
        string system = TaskAssistant.SystemPrompt(DefaultHour);
        string user = TaskAssistant.UserPrompt(Store, message, source, history);

        var engines = new List<(string Name, Func<Task<string>> Ask)>();
        if (gemini != null) {
            engines.Add(("gemini", () => gemini.GenerateJsonAsync(system, user, cancel)));
        }
        if (webModel != null && engine != "gemini") {
            engines.Add(("web", () => webModel(TaskAssistant.CombinedPrompt(system, user))));
        }

        AssistantResult? result = null;
        foreach (var (name, ask) in engines) {
            try {
                string raw = await ask();
                var plan = TaskAssistant.Parse(raw);
                if (plan == null) {
                    Log.Write("assistant(" + name + "): no JSON in reply (" + raw.Length + " chars)");
                    continue;
                }
                Log.Write("assistant(" + name + "): " + plan.Actions.Count + " action(s) from " + source);
                result = TaskAssistant.Apply(Store, plan, message, source, DefaultHour);
                break;
            } catch (OperationCanceledException) when (cancel.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                Log.Write("assistant(" + name + "): failed: " + ex.Message);
            }
        }
        if (result == null) {
            result = TaskAssistant.SaveAsIdea(Store, message, source);
            result.Reply = engines.Count == 0
                ? Loc.T("Saved as an idea. Add a Gemini API key to let EchoType understand dates, projects and commands.")
                : Loc.T("Couldn't reach the assistant — saved as an idea.");
        }
        RememberTurn("user", message);
        string touched = result.TouchedIds.Count > 0 ? " [ids " + string.Join(", ", result.TouchedIds) + "]" : "";
        RememberTurn("assistant", result.FullText() + touched);
        OnUi(CheckReminders);
        return result;
    }

    private void RememberTurn(string role, string text) {
        lock (_historyGate) {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastTurnAt > HistoryLifetime) {
                _history.Clear();
            }
            _lastTurnAt = now;
            _history.Add(new AssistantTurn(role, text.Length > 600 ? text[..600] : text));
            if (_history.Count > MaxHistoryTurns) {
                _history.RemoveRange(0, _history.Count - MaxHistoryTurns);
            }
        }
    }

    private List<AssistantTurn> HistorySnapshot() {
        lock (_historyGate) {
            if (DateTimeOffset.UtcNow - _lastTurnAt > HistoryLifetime) {
                _history.Clear();
            }
            return [.. _history];
        }
    }

    // ── Reminders ─────────────────────────────────────────────

    /// <summary>Fires due reminders, wakes snoozed ones and repeats the chime of unanswered ones.</summary>
    public void CheckReminders() {
        if (_marshal.InvokeRequired) {
            OnUi(CheckReminders);
            return;
        }
        var now = TaskTime.UtcNow;
        var fired = new List<Notice>();
        var woke = new List<Notice>();
        Store.Change("reminders", c => {
            foreach (var item in c.Items.Where(i => i.IsOpen && i.Due is { } d && d <= now).ToList()) {
                var due = item.Due!.Value;
                if (c.Notices.Any(n => n.TaskId == item.Id && n.Occurrence == due)) {
                    continue;
                }
                var notice = c.AddNotice(new Notice {
                    TaskId = item.Id,
                    Title = item.Title,
                    Occurrence = due,
                    FiredAt = now,
                    Late = now - due > LateAfter,
                });
                fired.Add(notice);
            }
            foreach (var notice in c.Notices.Where(n => n.State == NoticeState.Snoozed && n.SnoozeUntil <= now).ToList()) {
                c.EditNotice(notice, n => {
                    n.State = NoticeState.Active;
                    n.SnoozeUntil = null;
                    n.FiredAt = now;
                });
                woke.Add(notice);
            }
        });
        foreach (var notice in fired.Concat(woke)) {
            Log.Write("reminder: " + (notice.Late ? "late " : "") + "\"" + notice.Title + "\"");
            ShowToast(notice);
            _lastChime[notice.Id] = now;
            Sounds.Reminder();
            _ = SendReminderToTelegramAsync(notice);
        }
        int repeat = _settings.ReminderRepeatMinutes;
        if (repeat > 0 && fired.Count == 0 && woke.Count == 0) {
            var active = Store.Notices().Where(n => n.State == NoticeState.Active).ToList();
            foreach (var notice in active) {
                var last = _lastChime.TryGetValue(notice.Id, out var t) ? t : notice.FiredAt;
                if (now - last >= TimeSpan.FromMinutes(repeat)) {
                    _lastChime[notice.Id] = now;
                    Sounds.Reminder();
                    ShowToast(notice);
                    break; // one chime per check is enough
                }
            }
        }
    }

    private void ShowToast(Notice notice) {
        var item = Store.Find(notice.TaskId);
        var projects = item == null ? [] : Store.Projects().Where(p => item.ProjectIds.Contains(p.Id)).Select(p => p.Name).ToList();
        ReminderToast.Show(notice, item, projects, action => ResolveNotice(notice.Id, action, "app"));
    }

    /// <summary>Done / snooze / stop / delete from the toast, the notification center, Telegram or voice.</summary>
    public void ResolveNotice(string noticeId, NoticeAction action, string from) {
        if (_marshal.InvokeRequired) {
            OnUi(() => ResolveNotice(noticeId, action, from));
            return;
        }
        var notice = Store.FindNotice(n => n.Id == noticeId);
        if (notice == null) {
            return;
        }
        var item = Store.Find(notice.TaskId);
        Log.Write("reminder: " + action + " \"" + notice.Title + "\" from " + from);
        switch (action) {
            case NoticeAction.Done:
                if (item != null && item.IsOpen) {
                    Store.Change(Loc.F("Complete \"{0}\"", TaskAssistant.Short(notice.Title)), c => c.Complete(item.Id, from));
                } else {
                    Store.Change("reminder", c => c.CloseNotices(notice.TaskId, NoticeState.Done, from));
                }
                break;
            case NoticeAction.Delete:
                if (item != null) {
                    Store.Change(Loc.F("Delete \"{0}\"", TaskAssistant.Short(notice.Title)), c => c.Remove(item.Id, from));
                } else {
                    Store.Change("reminder", c => c.CloseNotices(notice.TaskId, NoticeState.Deleted, from));
                }
                break;
            case NoticeAction.Stop:
                Store.Change(Loc.F("Stop reminder \"{0}\"", TaskAssistant.Short(notice.Title)), c => {
                    c.CloseNotices(notice.TaskId, NoticeState.Dismissed, from);
                    // A repeating task skips to its next time so the series keeps reminding.
                    if (item is { Recurrence: { } rule, Due: { } due }) {
                        var dueLocal = TaskTime.ToLocal(due);
                        var after = dueLocal > TaskTime.LocalNow ? dueLocal : TaskTime.LocalNow;
                        if (RecurrenceMath.Next(rule, dueLocal, after) is { } next) {
                            c.Edit(item.Id, i => i.Due = TaskTime.FromLocal(next));
                        }
                    }
                });
                break;
            default: {
                var until = action switch {
                    NoticeAction.Snooze10 => TaskTime.UtcNow.AddMinutes(10),
                    NoticeAction.SnoozeTomorrow => TaskTime.FromLocal(TaskTime.LocalNow.Date.AddDays(1).AddHours(DefaultHour)),
                    _ => TaskTime.UtcNow.AddHours(1),
                };
                long? pollMessage = notice.TelegramMessageId;
                Store.Change("snooze", c => {
                    foreach (var n in c.Notices.Where(n => n.TaskId == notice.TaskId && n.IsOpen).ToList()) {
                        c.EditNotice(n, x => {
                            x.State = NoticeState.Snoozed;
                            x.SnoozeUntil = until;
                            x.TelegramMessageId = null;
                            x.TelegramPollId = null;
                        });
                    }
                });
                ReminderToast.Close(notice.Id);
                string text = "⏰ " + notice.Title + " — " + Loc.F("snoozed until {0}", TaskTime.Describe(until, true));
                if (from != "telegram") {
                    Notify?.Invoke(text, OverlayKind.Info);
                }
                _ = TelegramAfterResolveAsync(pollMessage, from == "telegram" ? text : text + " (" + Loc.T("from PC") + ")");
                break;
            }
        }
    }

    private void OnNoticesClosed(IReadOnlyList<Notice> notices) {
        foreach (var notice in notices) {
            ReminderToast.Close(notice.Id);
            _lastChime.Remove(notice.Id);
            if (notice.TelegramMessageId is { } messageId) {
                string icon = notice.State switch {
                    NoticeState.Done => "✅",
                    NoticeState.Deleted => "🗑",
                    _ => "🔕",
                };
                string text = icon + " " + notice.Title;
                if (notice.ResolvedFrom != "telegram") {
                    text += " (" + Loc.T("from PC") + ")";
                }
                _ = TelegramAfterResolveAsync(messageId, text);
            }
        }
    }

    // ── Telegram ──────────────────────────────────────────────

    public bool TelegramConfigured =>
        _settings.TelegramEnabled && _settings.TelegramBotToken.Trim().Length > 0;

    public bool TelegramRunning => _bot != null;

    /// <summary>(Re)starts the bot after its settings change.</summary>
    public void ApplyTelegramSettings() {
        string token = _settings.TelegramBotToken.Trim();
        TelegramBot.TryParseChatId(_settings.TelegramChatId, out long chatId);
        string key = _settings.TelegramEnabled && token.Length > 0 ? token + "|" + chatId : "";
        if (key == _botKey) {
            return;
        }
        _bot?.Dispose();
        _bot = null;
        _botKey = key;
        if (key.Length == 0) {
            Log.Write("telegram: off");
            Changed?.Invoke();
            return;
        }
        // Without a chat id the bot only answers "your chat id is …" so it can be set up.
        var bot = new TelegramBot(token, chatId);
        bot.MessageReceived += OnTelegramMessageAsync;
        bot.PollAnswered += OnTelegramPollAnswered;
        bot.Start(() => Store.TelegramOffset, offset => Store.TelegramOffset = offset);
        _bot = bot;
        Log.Write("telegram: on (chat " + (chatId == 0 ? "not set" : "set") + ")");
        Changed?.Invoke();
    }

    private async Task SendReminderToTelegramAsync(Notice notice) {
        var bot = _bot;
        if (bot == null || bot.ChatId == 0 || !_settings.TelegramReminders) {
            return;
        }
        var item = Store.Find(notice.TaskId);
        var sb = new StringBuilder("⏰ ").Append(notice.Title);
        if (item != null) {
            sb.Append('\n').Append(TaskTime.Describe(notice.Occurrence, item.HasTime));
            if (item.Recurrence != null) {
                sb.Append(" · 🔁 ").Append(RecurrenceMath.Describe(item.Recurrence));
            }
            var names = Store.Projects().Where(p => item.ProjectIds.Contains(p.Id)).Select(p => p.Name).ToList();
            if (names.Count > 0) {
                sb.Append(" · 📁 ").Append(string.Join(", ", names));
            }
        }
        if (notice.Late) {
            sb.Append('\n').Append(Loc.T("(late — EchoType wasn't running)"));
        }
        try {
            var (messageId, pollId) = await bot.SendPollAsync(sb.ToString(), PollOptions());
            Store.Change("telegram", c => {
                if (c.FindNotice(notice.Id) is { } n) {
                    c.EditNotice(n, x => {
                        x.TelegramMessageId = messageId;
                        x.TelegramPollId = pollId;
                    });
                }
            });
            // Resolved on the PC while the poll was being sent: close it right away.
            if (Store.FindNotice(n => n.Id == notice.Id) is { IsOpen: false }) {
                await bot.StopPollAsync(messageId);
            }
        } catch (Exception ex) {
            Log.Write("telegram: reminder poll failed: " + ex.Message);
        }
    }

    private static string[] PollOptions() => [
        "✅ " + Loc.T("Done"),
        "⏰ " + Loc.T("Remind me in 1 hour"),
        "🌙 " + Loc.T("Tomorrow"),
        "🔕 " + Loc.T("Stop reminder"),
        "🗑 " + Loc.T("Delete task"),
    ];

    private static NoticeAction ActionForOption(int option) => option switch {
        0 => NoticeAction.Done,
        1 => NoticeAction.Snooze60,
        2 => NoticeAction.SnoozeTomorrow,
        3 => NoticeAction.Stop,
        _ => NoticeAction.Delete,
    };

    private void OnTelegramPollAnswered(string pollId, int option) {
        var notice = Store.FindNotice(n => n.TelegramPollId == pollId);
        if (notice == null) {
            Log.Write("telegram: vote for an unknown poll");
            return;
        }
        if (!notice.IsOpen) {
            _ = TelegramAfterResolveAsync(notice.TelegramMessageId, Loc.T("Already handled on the PC."));
            return;
        }
        ResolveNotice(notice.Id, ActionForOption(option), "telegram");
    }

    private async Task TelegramAfterResolveAsync(long? pollMessageId, string text) {
        var bot = _bot;
        if (bot == null || bot.ChatId == 0) {
            return;
        }
        try {
            if (pollMessageId is { } id) {
                await bot.StopPollAsync(id);
            }
            await bot.SendMessageAsync(text, pollMessageId);
        } catch (Exception ex) {
            Log.Write("telegram: follow-up failed: " + ex.Message);
        }
    }

    private async Task OnTelegramMessageAsync(TelegramIncoming message) {
        var bot = _bot;
        if (bot == null) {
            return;
        }
        string text = message.Text.Trim();
        if (message.AudioFileId == null && text.StartsWith('/')) {
            await bot.SendMessageAsync(Command(text), message.MessageId);
            return;
        }
        await bot.TypingAsync();
        if (message.AudioFileId != null) {
            var gemini = GeminiClient.FromSettings(_settings);
            if (gemini == null) {
                await bot.SendMessageAsync(Loc.T("Add a Gemini API key in EchoType → Tasks & sync to turn voice notes into text."),
                    message.MessageId);
                return;
            }
            try {
                byte[] audio = await bot.DownloadFileAsync(message.AudioFileId);
                string transcript = await gemini.TranscribeAsync(audio, message.AudioMime, CancellationToken.None);
                transcript = ApplyWordReplacements(transcript);
                Log.Write("telegram: voice note transcribed (" + audio.Length + " bytes → " + transcript.Length + " chars)");
                if (transcript.Length == 0) {
                    await bot.SendMessageAsync(Loc.T("I couldn't hear anything in that voice note."), message.MessageId);
                    return;
                }
                await bot.SendMessageAsync("🎙 " + transcript, message.MessageId);
                text = (text.Length > 0 ? text + "\n" : "") + transcript;
            } catch (Exception ex) {
                Log.Write("telegram: voice note failed: " + ex.Message);
                await bot.SendMessageAsync(Loc.F("Couldn't transcribe the voice note: {0}", ex.Message), message.MessageId);
                return;
            }
        }
        var result = await RunAssistantAsync(text, "telegram");
        await bot.SendMessageAsync(result.FullText().Length > 0 ? result.FullText() : Loc.T("Nothing to change."),
            message.MessageId);
        if (result.Changed) {
            OnUi(() => Notify?.Invoke("📱 " + (result.Lines.FirstOrDefault() ?? result.Reply), OverlayKind.Info));
        }
    }

    private string ApplyWordReplacements(string text) =>
        WordReplacement.Apply(text, _settings.WordReplacements);

    /// <summary>Bot commands: /tasks, /inbox, /projects, /undo, /help.</summary>
    private string Command(string text) {
        string command = text.Split(' ', 2)[0].Split('@')[0].ToLowerInvariant();
        switch (command) {
            case "/tasks":
            case "/today":
                return TasksSummary();
            case "/inbox":
                return InboxSummary();
            case "/projects":
                return ProjectsSummary();
            case "/undo": {
                string? undone = Store.Undo();
                return undone == null ? Loc.T("Nothing to undo.") : "↩ " + Loc.F("Undone: {0}", undone);
            }
            default:
                return Loc.T("Send me a text or a voice note: ideas go to the Inbox (or their project), and things like \"remind me Thursday at 5 to call Ali\", \"every Sunday and Tuesday\", \"delete it\" or \"undo\" are understood. Reminders arrive here as polls: vote Done, Snooze, Stop or Delete, and EchoType on the PC follows.")
                    + "\n\n/tasks /inbox /projects /undo";
        }
    }

    public string TasksSummary() {
        var projects = Store.Projects();
        var open = Store.Items().Where(i => i.IsOpen && i.Due != null).OrderBy(i => i.Due).ToList();
        var today = TaskTime.LocalNow.Date;
        var sb = new StringBuilder();
        void Section(string title, IEnumerable<TaskItem> items) {
            var list = items.ToList();
            if (list.Count == 0) {
                return;
            }
            sb.Append(title).Append('\n');
            foreach (var item in list.Take(25)) {
                sb.Append("• ").Append(TaskAssistant.Line(item, projects)).Append('\n');
            }
            sb.Append('\n');
        }
        Section("⚠️ " + Loc.T("Overdue"), open.Where(i => TaskTime.ToLocal(i.Due!.Value) < TaskTime.LocalNow && TaskTime.ToLocal(i.Due!.Value).Date < today));
        Section("📅 " + Loc.T("Today"), open.Where(i => TaskTime.ToLocal(i.Due!.Value).Date == today));
        Section("🗓 " + Loc.T("Next 7 days"), open.Where(i => TaskTime.ToLocal(i.Due!.Value).Date > today
            && TaskTime.ToLocal(i.Due!.Value).Date <= today.AddDays(7)));
        return sb.Length == 0 ? Loc.T("No tasks with a date in the next 7 days.") : sb.ToString().TrimEnd();
    }

    private string InboxSummary() {
        var projects = Store.Projects();
        var inbox = Store.Items().Where(i => i.IsOpen && i.ProjectIds.Count == 0).OrderByDescending(i => i.CreatedAt).ToList();
        if (inbox.Count == 0) {
            return Loc.T("The Inbox is empty.");
        }
        var sb = new StringBuilder("📥 " + Loc.T("Inbox") + "\n");
        foreach (var item in inbox.OrderBy(i => i.Kind).Take(40)) {
            sb.Append("• ").Append(TaskAssistant.Line(item, projects)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private string ProjectsSummary() {
        var items = Store.Items().Where(i => i.IsOpen).ToList();
        var projects = Store.Projects();
        if (projects.Count == 0) {
            return Loc.T("No projects yet. Create them in EchoType, or say \"create a project called …\".");
        }
        var sb = new StringBuilder();
        foreach (var project in projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)) {
            var mine = items.Where(i => i.ProjectIds.Contains(project.Id)).ToList();
            sb.Append("📁 ").Append(project.Name).Append(" — ")
                .Append(Loc.F("{0} ideas, {1} tasks", mine.Count(i => i.Kind == ItemKind.Idea), mine.Count(i => i.Kind == ItemKind.Task)))
                .Append('\n');
            foreach (var item in mine.OrderBy(i => i.Kind).Take(8)) {
                sb.Append("   • ").Append(item.Kind == ItemKind.Task ? "📌 " : "💡 ").Append(item.Title).Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatCount(int n) => n.ToString(CultureInfo.CurrentCulture);
}
