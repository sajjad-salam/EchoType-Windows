using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoType.Tasks;

/// <summary>Everything in tasks.json.</summary>
internal sealed class TaskData {

    [JsonPropertyName("items")]
    public List<TaskItem> Items { get; set; } = [];

    [JsonPropertyName("projects")]
    public List<Project> Projects { get; set; } = [];

    [JsonPropertyName("notices")]
    public List<Notice> Notices { get; set; } = [];

    [JsonPropertyName("undo")]
    public List<UndoEntry> Undo { get; set; } = [];

    /// <summary>Next Telegram update to ask for, so messages are handled once.</summary>
    [JsonPropertyName("telegramOffset")]
    public long TelegramOffset { get; set; }
}

/// <summary>One undoable change: the state of every touched item/project before it.</summary>
internal sealed class UndoEntry {

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Item id → item before the change (null = it didn't exist).</summary>
    [JsonPropertyName("items")]
    public Dictionary<string, TaskItem?> Items { get; set; } = [];

    [JsonPropertyName("projects")]
    public Dictionary<string, Project?> Projects { get; set; } = [];
}

/// <summary>
/// The ideas/tasks database (%APPDATA%\EchoType\tasks.json). Thread-safe: the window, the
/// reminder timer and the Telegram poller all change it through <see cref="Change{T}"/>,
/// which records an undo step and saves. <see cref="Changed"/> fires on the calling thread.
/// </summary>
internal sealed class TaskStore {

    private const int MaxUndo = 40;
    private const int MaxNoticeHistory = 300;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private TaskData _data;

    public event Action? Changed;

    /// <summary>Reminders closed by a change (done, stopped, deleted), raised after it is saved.</summary>
    public event Action<IReadOnlyList<Notice>>? NoticesClosed;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoType", "tasks.json");

    public TaskStore(string? path = null) {
        _path = path ?? DefaultPath;
        _data = Load(_path);
    }

    private static TaskData Load(string path) {
        try {
            if (File.Exists(path)) {
                var data = JsonSerializer.Deserialize<TaskData>(File.ReadAllText(path), JsonOptions) ?? new TaskData();
                data.Items ??= [];
                data.Projects ??= [];
                data.Notices ??= [];
                data.Undo ??= [];
                data.Items.RemoveAll(i => i == null || string.IsNullOrEmpty(i.Id));
                foreach (var item in data.Items) {
                    item.Title ??= "";
                    item.Notes ??= "";
                    item.ProjectIds ??= [];
                    item.Source ??= "app";
                    if (item.Recurrence != null) {
                        item.Recurrence.Days ??= [];
                        item.Recurrence.Interval = Math.Max(1, item.Recurrence.Interval);
                    }
                }
                data.Projects.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Id));
                foreach (var project in data.Projects) {
                    project.Name ??= "";
                    project.Keywords ??= [];
                }
                data.Notices.RemoveAll(n => n == null);
                return data;
            }
        } catch (Exception ex) {
            Log.Write("tasks: failed to load " + path + " (" + ex.Message + ")");
            try {
                File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
            } catch {
                // keep going with an empty list; the broken file stays for recovery
            }
        }
        return new TaskData();
    }

    private void SaveLocked() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        } catch (Exception ex) {
            Log.Write("tasks: failed to save: " + ex.Message);
        }
    }

    // ── Reading ───────────────────────────────────────────────

    public List<TaskItem> Items() {
        lock (_gate) {
            return [.. _data.Items];
        }
    }

    public List<Project> Projects() {
        lock (_gate) {
            return [.. _data.Projects];
        }
    }

    public List<Notice> Notices() {
        lock (_gate) {
            return [.. _data.Notices];
        }
    }

    public TaskItem? Find(string id) {
        lock (_gate) {
            return _data.Items.FirstOrDefault(i => i.Id == id);
        }
    }

    public Project? FindProject(string id) {
        lock (_gate) {
            return _data.Projects.FirstOrDefault(p => p.Id == id);
        }
    }

    public Notice? FindNotice(Func<Notice, bool> match) {
        lock (_gate) {
            return _data.Notices.FirstOrDefault(match);
        }
    }

    public string? LastUndoDescription {
        get {
            lock (_gate) {
                return _data.Undo.Count == 0 ? null : _data.Undo[^1].Description;
            }
        }
    }

    public long TelegramOffset {
        get {
            lock (_gate) {
                return _data.TelegramOffset;
            }
        }
        set {
            lock (_gate) {
                if (_data.TelegramOffset == value) {
                    return;
                }
                _data.TelegramOffset = value;
                SaveLocked();
            }
        }
    }

    // ── Writing ───────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="work"/> under the store lock. Item and project edits made through
    /// the <see cref="TaskChange"/> become one undo step named <paramref name="description"/>;
    /// notice edits are not undoable. Saves and raises <see cref="Changed"/> when anything changed.
    /// </summary>
    public T Change<T>(string description, Func<TaskChange, T> work) {
        T result;
        bool changed;
        List<Notice> closed;
        lock (_gate) {
            var change = new TaskChange(_data);
            result = work(change);
            changed = change.Dirty;
            closed = change.ClosedNotices;
            if (change.Undo.Items.Count > 0 || change.Undo.Projects.Count > 0) {
                change.Undo.Description = description;
                _data.Undo.Add(change.Undo);
                if (_data.Undo.Count > MaxUndo) {
                    _data.Undo.RemoveRange(0, _data.Undo.Count - MaxUndo);
                }
            }
            if (changed) {
                TrimNoticesLocked();
                SaveLocked();
            }
        }
        if (changed) {
            RaiseChanged();
        }
        if (closed.Count > 0) {
            try {
                NoticesClosed?.Invoke(closed);
            } catch (Exception ex) {
                Log.Write("tasks: notice listener failed: " + ex.Message);
            }
        }
        return result;
    }

    public void Change(string description, Action<TaskChange> work) =>
        Change<object?>(description, c => {
            work(c);
            return null;
        });

    /// <summary>Reverts the last undoable change. Returns its description, or null when there is none.</summary>
    public string? Undo() {
        string? description;
        lock (_gate) {
            if (_data.Undo.Count == 0) {
                return null;
            }
            var entry = _data.Undo[^1];
            _data.Undo.RemoveAt(_data.Undo.Count - 1);
            foreach (var (id, before) in entry.Items) {
                int index = _data.Items.FindIndex(i => i.Id == id);
                if (before == null) {
                    if (index >= 0) {
                        _data.Items.RemoveAt(index);
                    }
                } else if (index >= 0) {
                    _data.Items[index] = before;
                } else {
                    _data.Items.Add(before);
                }
            }
            foreach (var (id, before) in entry.Projects) {
                int index = _data.Projects.FindIndex(p => p.Id == id);
                if (before == null) {
                    if (index >= 0) {
                        _data.Projects.RemoveAt(index);
                    }
                } else if (index >= 0) {
                    _data.Projects[index] = before;
                } else {
                    _data.Projects.Add(before);
                }
            }
            // Reminders of items that came back stay in history; open ones of removed items close.
            var live = _data.Items.Where(i => !i.Completed).Select(i => i.Id).ToHashSet();
            foreach (var notice in _data.Notices.Where(n => n.IsOpen && !live.Contains(n.TaskId))) {
                notice.State = NoticeState.Deleted;
                notice.ResolvedAt = DateTimeOffset.UtcNow;
            }
            description = entry.Description;
            SaveLocked();
        }
        RaiseChanged();
        return description;
    }

    private void TrimNoticesLocked() {
        int closed = _data.Notices.Count(n => !n.IsOpen);
        if (closed <= MaxNoticeHistory) {
            return;
        }
        var drop = _data.Notices.Where(n => !n.IsOpen)
            .OrderBy(n => n.ResolvedAt ?? n.FiredAt)
            .Take(closed - MaxNoticeHistory)
            .ToHashSet();
        _data.Notices.RemoveAll(drop.Contains);
    }

    private void RaiseChanged() {
        try {
            Changed?.Invoke();
        } catch (Exception ex) {
            Log.Write("tasks: change listener failed: " + ex.Message);
        }
    }

    internal static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}

/// <summary>Mutations inside one <see cref="TaskStore.Change{T}"/> call.</summary>
internal sealed class TaskChange {

    private readonly TaskData _data;

    internal UndoEntry Undo { get; } = new();
    internal bool Dirty { get; private set; }
    internal List<Notice> ClosedNotices { get; } = [];

    internal TaskChange(TaskData data) {
        _data = data;
    }

    public IReadOnlyList<TaskItem> Items => _data.Items;
    public IReadOnlyList<Project> Projects => _data.Projects;
    public IReadOnlyList<Notice> Notices => _data.Notices;

    public TaskItem? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _data.Items.FirstOrDefault(i => i.Id == id.Trim());

    public Project? FindProject(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _data.Projects.FirstOrDefault(p => p.Id == id.Trim());

    private void Remember(TaskItem? before, string id) {
        if (!Undo.Items.ContainsKey(id)) {
            Undo.Items[id] = before == null ? null : TaskStore.Clone(before);
        }
        Dirty = true;
    }

    private void Remember(Project? before, string id) {
        if (!Undo.Projects.ContainsKey(id)) {
            Undo.Projects[id] = before == null ? null : TaskStore.Clone(before);
        }
        Dirty = true;
    }

    public TaskItem Add(TaskItem item) {
        Remember((TaskItem?)null, item.Id);
        item.CreatedAt = item.UpdatedAt = DateTimeOffset.UtcNow;
        _data.Items.Add(item);
        return item;
    }

    public TaskItem? Edit(string id, Action<TaskItem> edit) {
        var item = Find(id);
        if (item == null) {
            return null;
        }
        Remember(item, item.Id);
        edit(item);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        return item;
    }

    public TaskItem? Remove(string id, string from = "app") {
        var item = Find(id);
        if (item == null) {
            return null;
        }
        Remember(item, item.Id);
        _data.Items.Remove(item);
        CloseNotices(item.Id, NoticeState.Deleted, from);
        return item;
    }

    /// <summary>
    /// Marks a task done. A repeating task moves on to its next occurrence instead (the
    /// first one still ahead, so a series that was missed for days doesn't fire for each).
    /// Returns the next due for a repeating task, otherwise null.
    /// </summary>
    public DateTimeOffset? Complete(string id, string from) {
        DateTimeOffset? next = null;
        Edit(id, item => {
            if (item.Recurrence != null && item.Due is { } due) {
                var dueLocal = TaskTime.ToLocal(due);
                var nowLocal = TaskTime.LocalNow;
                var after = dueLocal > nowLocal ? dueLocal : nowLocal;
                if (RecurrenceMath.Next(item.Recurrence, dueLocal, after) is { } nextLocal) {
                    next = TaskTime.FromLocal(nextLocal);
                    item.Due = next;
                    return;
                }
            }
            item.Completed = true;
            item.CompletedAt = DateTimeOffset.UtcNow;
        });
        CloseNotices(id, NoticeState.Done, from);
        return next;
    }

    public void Reopen(string id) =>
        Edit(id, item => {
            item.Completed = false;
            item.CompletedAt = null;
        });

    public Project AddProject(string name, IEnumerable<string>? keywords = null) {
        var project = new Project {
            Name = name.Trim(),
            Keywords = keywords?.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct().ToList() ?? [],
        };
        Remember((Project?)null, project.Id);
        _data.Projects.Add(project);
        return project;
    }

    public Project? EditProject(string id, Action<Project> edit) {
        var project = FindProject(id);
        if (project == null) {
            return null;
        }
        Remember(project, project.Id);
        edit(project);
        return project;
    }

    /// <summary>Deletes a project and unlinks its items (the items themselves stay).</summary>
    public Project? RemoveProject(string id) {
        var project = FindProject(id);
        if (project == null) {
            return null;
        }
        Remember(project, project.Id);
        _data.Projects.Remove(project);
        foreach (var item in _data.Items.Where(i => i.ProjectIds.Contains(id)).ToList()) {
            Edit(item.Id, i => i.ProjectIds.Remove(id));
        }
        return project;
    }

    // ── Notices (not undoable) ────────────────────────────────

    public Notice AddNotice(Notice notice) {
        _data.Notices.Add(notice);
        Dirty = true;
        return notice;
    }

    public Notice? FindNotice(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _data.Notices.FirstOrDefault(n => n.Id == id);

    public void EditNotice(Notice notice, Action<Notice> edit) {
        edit(notice);
        Dirty = true;
    }

    /// <summary>Closes every open reminder of a task.</summary>
    public List<Notice> CloseNotices(string taskId, NoticeState state, string from) {
        var closed = _data.Notices.Where(n => n.TaskId == taskId && n.IsOpen).ToList();
        foreach (var notice in closed) {
            notice.State = state;
            notice.ResolvedAt = DateTimeOffset.UtcNow;
            notice.ResolvedFrom = from;
            Dirty = true;
            ClosedNotices.Add(notice);
        }
        return closed;
    }

    public void ClearNoticeHistory() {
        int removed = _data.Notices.RemoveAll(n => !n.IsOpen);
        Dirty |= removed > 0;
    }
}
