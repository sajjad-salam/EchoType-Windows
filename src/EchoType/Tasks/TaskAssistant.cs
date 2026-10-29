using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using EchoType.Ui;

namespace EchoType.Tasks;

/// <summary>A turn of the running conversation with the tasks assistant (voice, Telegram, quick bar).</summary>
internal sealed record AssistantTurn(string Role, string Text);

/// <summary>What one message did.</summary>
internal sealed class AssistantResult {
    public string Reply { get; set; } = "";
    public List<string> Lines { get; } = [];
    public List<string> TouchedIds { get; } = [];
    public bool Changed { get; set; }
    /// <summary>The model could not be reached and the text was saved as a plain idea.</summary>
    public bool UsedFallback { get; set; }

    /// <summary>Reply plus the per-action lines, for Telegram and the HUD.</summary>
    public string FullText() {
        var reply = Reply.Trim();
        if (Lines.Count == 0) {
            return reply;
        }
        string lines = string.Join("\n", Lines);
        return reply.Length == 0 ? lines : reply + "\n" + lines;
    }
}

/// <summary>
/// Turns free speech or text ("add a task for Thursday at 5 to call Ali", "ضيف فكرة لمشروع الجوكر",
/// "delete it", "undo") into changes to the <see cref="TaskStore"/>. A language model (Gemini via
/// API, or the cleaning model's web window) reads the message plus the current projects, open
/// items and the last few turns, and answers with JSON actions that are applied here as one
/// undo step. Without a model the text is saved as an idea, linked to projects by name.
/// </summary>
internal static class TaskAssistant {

    private const int MaxContextItems = 120;

    private static readonly JsonSerializerOptions RelaxedJson = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string SystemPrompt(int defaultHour) {
        string hour = defaultHour.ToString("00", CultureInfo.InvariantCulture) + ":00";
        return
            "You manage the user's ideas and tasks inside the EchoType app. The user writes or speaks Iraqi Arabic, "
            + "Arabic or English, often mixed, and the text may come from speech recognition with small mistakes. "
            + "Turn the message into actions on their list.\n"
            + "RULES\n"
            + "- kind: \"task\" when there is something to do with a date, time, deadline or repeat, or the user says "
            + "task / مهمة / ذكرني / remind me. Otherwise (a thought, plan, feature idea, note) \"idea\". An explicit "
            + "\"idea / فكرة\" or \"task / مهمة\" always wins.\n"
            + "- title: short and clean, in the user's language, without command words (\"add a task\", \"ضيفلي\", "
            + "\"ذكرني\", \"سجل فكرة\") or the date words. Put long extra detail in notes.\n"
            + "- projects: link every project the item is about, by name, keyword or obvious meaning, in any spelling "
            + "or script (e.g. جوكر = Joker). One item may belong to several projects: list all their ids once, never "
            + "add duplicates. Only when the user names a project that is not in the list, or asks to create one, put "
            + "its name in new_projects.\n"
            + "- due: the user's local time as \"YYYY-MM-DDTHH:mm\", or \"YYYY-MM-DD\" when no time was said (the reminder "
            + "then fires at " + hour + "). morning 09:00, noon 12:00, afternoon/عصر 16:00, evening/مساء 18:00, night/بالليل 21:00. "
            + "Relative times (\"after two hours\", \"باچر\" = tomorrow, \"عقب باچر\" = day after tomorrow) count from the current "
            + "time. A weekday means the next one ahead; a time today that has already passed means tomorrow.\n"
            + "- repeat: {\"frequency\":\"daily|weekly|monthly|yearly\",\"interval\":1,\"days\":[\"sat\",\"sun\",...],\"until\":\"YYYY-MM-DD\" or null}. "
            + "\"three times a week\" without days → weekly on three spread days such as sun, tue, thu. Work days in Iraq "
            + "are sun–thu. Give the first occurrence as due when a time is known.\n"
            + "- Refer to existing items only by their id from the list. \"it / that / هاي / ياها\" means the item most "
            + "recently created or mentioned in the conversation.\n"
            + "- \"undo / تراجع / رجعها / cancel what you just added\" → {\"type\":\"undo\"} (once per step to undo).\n"
            + "- Delete or complete only items the user clearly means. If unsure which, do nothing and ask in reply.\n"
            + "- Questions (\"what do I have today?\", \"شنو مهامي باچر\") → no actions; answer in reply from the list.\n"
            + "- A message that is just a thought → one add action with kind idea.\n"
            + "OUTPUT only one JSON object: {\"reply\":\"...\",\"actions\":[...]}\n"
            + "Actions:\n"
            + "{\"type\":\"add\",\"kind\":\"idea|task\",\"title\":\"\",\"notes\":\"\",\"projects\":[\"id\"],\"new_projects\":[\"name\"],\"due\":null,\"repeat\":null}\n"
            + "{\"type\":\"update\",\"id\":\"\", then only what changes: \"title\",\"notes\",\"kind\",\"projects\" (full list),\"add_projects\",\"remove_projects\",\"due\" (null clears),\"repeat\" (null clears)}\n"
            + "{\"type\":\"complete\",\"id\":\"\"}  {\"type\":\"reopen\",\"id\":\"\"}  {\"type\":\"delete\",\"id\":\"\"}\n"
            + "{\"type\":\"snooze\",\"id\":\"\",\"minutes\":60}  {\"type\":\"stop_reminder\",\"id\":\"\"}\n"
            + "{\"type\":\"add_project\",\"name\":\"\",\"keywords\":[\"other spellings\"]}  {\"type\":\"rename_project\",\"id\":\"\",\"name\":\"\"}  "
            + "{\"type\":\"delete_project\",\"id\":\"\"}\n"
            + "{\"type\":\"undo\"}\n"
            + "reply: one or two short friendly sentences in the user's language and dialect (Iraqi Arabic when they "
            + "used Arabic) saying what you did — title, project, when — or answering the question.";
    }

    /// <summary>The user part of the prompt: the clock, the list, the conversation and the new message.</summary>
    public static string UserPrompt(TaskStore store, string message, string source, IReadOnlyList<AssistantTurn> history) {
        var now = TaskTime.LocalNow;
        var projects = store.Projects();
        var items = store.Items();
        var context = new JsonObject {
            ["now"] = now.ToString("dddd yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
            ["timezone"] = TaskTime.Zone.Id + " (UTC" + FormatOffset(TaskTime.Zone.GetUtcOffset(now)) + ")",
            ["source"] = source,
        };
        var projectArray = new JsonArray();
        foreach (var p in projects) {
            var o = new JsonObject { ["id"] = p.Id, ["name"] = p.Name };
            if (p.Keywords.Count > 0) {
                o["keywords"] = new JsonArray(p.Keywords.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
            }
            projectArray.Add(o);
        }
        context["projects"] = projectArray;

        var open = items.Where(i => i.IsOpen)
            .OrderByDescending(i => i.UpdatedAt)
            .Take(MaxContextItems);
        var recentDone = items.Where(i => i.Completed)
            .OrderByDescending(i => i.CompletedAt)
            .Take(10);
        var itemArray = new JsonArray();
        foreach (var i in open.Concat(recentDone)) {
            var o = new JsonObject {
                ["id"] = i.Id,
                ["kind"] = i.Kind == ItemKind.Task ? "task" : "idea",
                ["title"] = i.Title,
            };
            if (i.ProjectIds.Count > 0) {
                o["projects"] = new JsonArray(i.ProjectIds.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
            }
            if (i.Due is { } due) {
                o["due"] = i.HasTime ? TaskTime.FormatIso(due) : TaskTime.ToLocal(due).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            if (i.Recurrence != null) {
                o["repeat"] = RepeatToJson(i.Recurrence);
            }
            if (i.Completed) {
                o["done"] = true;
            }
            itemArray.Add(o);
        }
        context["items"] = itemArray;

        if (history.Count > 0) {
            var turns = new JsonArray();
            foreach (var turn in history) {
                turns.Add(new JsonObject { ["role"] = turn.Role, ["text"] = turn.Text });
            }
            context["conversation"] = turns;
        }
        return "CONTEXT\n" + context.ToJsonString(RelaxedJson) + "\n\nMESSAGE\n" + message.Trim();
    }

    /// <summary>For the web-window model, which has no separate system prompt.</summary>
    public static string CombinedPrompt(string system, string user) =>
        system + "\n\n" + user + "\n\nAnswer with the JSON object only.";

    private static string FormatOffset(TimeSpan offset) =>
        (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

    // ── Parsing ───────────────────────────────────────────────

    public sealed class Plan {
        public string Reply { get; init; } = "";
        public List<JsonObject> Actions { get; init; } = [];
    }

    /// <summary>Reads the model's JSON, tolerating code fences or text around it. Null when there is none.</summary>
    public static Plan? Parse(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) {
            return null;
        }
        string text = raw.Trim();
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) {
            return null;
        }
        JsonNode? root;
        try {
            root = JsonNode.Parse(text[start..(end + 1)]);
        } catch (JsonException) {
            return null;
        }
        if (root is not JsonObject obj) {
            return null;
        }
        var actions = new List<JsonObject>();
        if (obj["actions"] is JsonArray array) {
            foreach (var node in array) {
                if (node is JsonObject action) {
                    actions.Add(action);
                }
            }
        }
        return new Plan { Reply = Str(obj, "reply") ?? "", Actions = actions };
    }

    // ── Applying ──────────────────────────────────────────────

    /// <summary>
    /// Applies a plan. "undo" actions run first (each reverts one earlier step); every other
    /// action lands in one new undo step, so "undo" afterwards reverts this whole message.
    /// </summary>
    public static AssistantResult Apply(TaskStore store, Plan plan, string message, string source, int defaultHour) {
        var result = new AssistantResult { Reply = plan.Reply };
        foreach (var undo in plan.Actions.Where(a => Type(a) == "undo")) {
            int steps = Math.Clamp(Int(undo, "steps") ?? 1, 1, 10);
            for (int i = 0; i < steps; i++) {
                string? undone = store.Undo();
                if (undone == null) {
                    result.Lines.Add("↩ " + Loc.T("Nothing to undo."));
                    break;
                }
                result.Changed = true;
                result.Lines.Add("↩ " + Loc.F("Undone: {0}", undone));
            }
        }
        var rest = plan.Actions.Where(a => Type(a) != "undo").ToList();
        if (rest.Count == 0) {
            return result;
        }
        int adds = rest.Count(a => Type(a) == "add");
        string description = Describe(rest, store);
        store.Change(description, change => {
            foreach (var action in rest) {
                try {
                    ApplyOne(change, action, adds == 1 ? message : null, source, defaultHour, result);
                } catch (Exception ex) {
                    Log.Write("assistant: action " + action.ToJsonString() + " failed: " + ex.Message);
                }
            }
        });
        return result;
    }

    /// <summary>No model: keep the text as an idea in the Inbox (or its projects).</summary>
    public static AssistantResult SaveAsIdea(TaskStore store, string text, string source) {
        var result = new AssistantResult { UsedFallback = true };
        var item = AddIdea(store, text, source);
        if (item != null) {
            result.Changed = true;
            result.TouchedIds.Add(item.Id);
            result.Lines.Add(Line(item, store.Projects()));
        }
        return result;
    }

    /// <summary>Quick add: plain text → idea, linked to every project it names.</summary>
    public static TaskItem? AddIdea(TaskStore store, string text, string source, ItemKind kind = ItemKind.Idea) {
        text = text.Trim();
        if (text.Length == 0) {
            return null;
        }
        return store.Change(Loc.F("Add \"{0}\"", Short(text)), change => {
            var item = new TaskItem {
                Kind = kind,
                Title = text,
                Source = source,
                ProjectIds = ProjectMatcher.Match(text, change.Projects).Select(p => p.Id).ToList(),
            };
            return change.Add(item);
        });
    }

    private static void ApplyOne(TaskChange change, JsonObject a, string? message, string source, int defaultHour,
        AssistantResult result) {
        switch (Type(a)) {
            case "add": {
                string title = (Str(a, "title") ?? "").Trim();
                if (title.Length == 0) {
                    return;
                }
                var item = new TaskItem {
                    Kind = Str(a, "kind")?.ToLowerInvariant() == "task" ? ItemKind.Task : ItemKind.Idea,
                    Title = title,
                    Notes = (Str(a, "notes") ?? "").Trim(),
                    Source = source,
                };
                item.ProjectIds = ResolveProjects(change, a["projects"], a["new_projects"], result);
                // Names the model missed still link (local spelling-tolerant match).
                string matchText = title + " " + item.Notes + " " + (message ?? "");
                foreach (var p in ProjectMatcher.Match(matchText, change.Projects)) {
                    if (!item.ProjectIds.Contains(p.Id)) {
                        item.ProjectIds.Add(p.Id);
                    }
                }
                ApplySchedule(item, a, defaultHour, isNew: true);
                if (item.Due != null || item.Recurrence != null) {
                    item.Kind = ItemKind.Task;
                }
                change.Add(item);
                result.Changed = true;
                result.TouchedIds.Add(item.Id);
                result.Lines.Add(Line(item, change.Projects));
                break;
            }
            case "update": {
                var target = change.Find(Str(a, "id"));
                if (target == null) {
                    return;
                }
                var item = change.Edit(target.Id, item => {
                    if (Str(a, "title") is { Length: > 0 } title) {
                        item.Title = title.Trim();
                    }
                    if (a.ContainsKey("notes")) {
                        item.Notes = (Str(a, "notes") ?? "").Trim();
                    }
                    if (Str(a, "kind") is { } kind) {
                        item.Kind = kind.ToLowerInvariant() == "task" ? ItemKind.Task : ItemKind.Idea;
                    }
                    if (a.ContainsKey("projects")) {
                        item.ProjectIds = ResolveProjects(change, a["projects"], a["new_projects"], result);
                    } else if (a["new_projects"] != null) {
                        foreach (var id in ResolveProjects(change, null, a["new_projects"], result)) {
                            if (!item.ProjectIds.Contains(id)) {
                                item.ProjectIds.Add(id);
                            }
                        }
                    }
                    foreach (var id in ResolveProjects(change, a["add_projects"], null, result)) {
                        if (!item.ProjectIds.Contains(id)) {
                            item.ProjectIds.Add(id);
                        }
                    }
                    foreach (var id in ResolveProjects(change, a["remove_projects"], null, result)) {
                        item.ProjectIds.Remove(id);
                    }
                    ApplySchedule(item, a, defaultHour, isNew: false);
                    if (item.Due != null || item.Recurrence != null) {
                        item.Kind = ItemKind.Task;
                    }
                });
                if (item != null) {
                    if (a.ContainsKey("due") || a.ContainsKey("repeat")) {
                        // Rescheduled: the old reminder no longer applies.
                        change.CloseNotices(item.Id, NoticeState.Dismissed, source);
                    }
                    result.Changed = true;
                    result.TouchedIds.Add(item.Id);
                    result.Lines.Add("✎ " + Line(item, change.Projects));
                }
                break;
            }
            case "complete": {
                var item = change.Find(Str(a, "id"));
                if (item == null) {
                    return;
                }
                var next = change.Complete(item.Id, source);
                result.Changed = true;
                result.TouchedIds.Add(item.Id);
                result.Lines.Add(next is { } n
                    ? "✅ " + item.Title + " — " + Loc.F("next: {0}", TaskTime.Describe(n, item.HasTime))
                    : "✅ " + item.Title);
                break;
            }
            case "reopen": {
                var item = change.Find(Str(a, "id"));
                if (item == null) {
                    return;
                }
                change.Reopen(item.Id);
                result.Changed = true;
                result.TouchedIds.Add(item.Id);
                result.Lines.Add("↺ " + item.Title);
                break;
            }
            case "delete": {
                var item = change.Remove(Str(a, "id") ?? "");
                if (item == null) {
                    return;
                }
                result.Changed = true;
                result.Lines.Add("🗑 " + item.Title);
                break;
            }
            case "snooze": {
                var item = change.Find(Str(a, "id"));
                if (item == null) {
                    return;
                }
                int minutes = Math.Clamp(Int(a, "minutes") ?? 60, 1, 60 * 24 * 30);
                var until = TaskTime.UtcNow.AddMinutes(minutes);
                var open = change.Notices.Where(n => n.TaskId == item.Id && n.IsOpen).ToList();
                if (open.Count > 0) {
                    foreach (var notice in open) {
                        change.EditNotice(notice, n => {
                            n.State = NoticeState.Snoozed;
                            n.SnoozeUntil = until;
                        });
                    }
                } else {
                    change.Edit(item.Id, i => {
                        i.Due = until;
                        i.HasTime = true;
                        i.Kind = ItemKind.Task;
                    });
                }
                result.Changed = true;
                result.TouchedIds.Add(item.Id);
                result.Lines.Add("⏰ " + item.Title + " — " + TaskTime.Describe(until, true));
                break;
            }
            case "stop_reminder": {
                var item = change.Find(Str(a, "id"));
                if (item == null) {
                    return;
                }
                if (change.CloseNotices(item.Id, NoticeState.Dismissed, source).Count > 0) {
                    result.Changed = true;
                    result.Lines.Add("🔕 " + item.Title);
                }
                break;
            }
            case "add_project": {
                string name = (Str(a, "name") ?? "").Trim();
                if (name.Length == 0 || FindProjectByName(change, name) != null) {
                    return;
                }
                var project = change.AddProject(name, Strings(a["keywords"]));
                result.Changed = true;
                result.Lines.Add("📁 " + project.Name);
                break;
            }
            case "rename_project": {
                string name = (Str(a, "name") ?? "").Trim();
                var project = FindProject(change, Str(a, "id"));
                if (project == null || name.Length == 0) {
                    return;
                }
                string old = project.Name;
                change.EditProject(project.Id, p => {
                    if (!p.Keywords.Contains(old, StringComparer.OrdinalIgnoreCase)) {
                        p.Keywords.Add(old);
                    }
                    p.Name = name;
                });
                result.Changed = true;
                result.Lines.Add("📁 " + old + " → " + name);
                break;
            }
            case "delete_project": {
                var project = FindProject(change, Str(a, "id"));
                if (project == null) {
                    return;
                }
                change.RemoveProject(project.Id);
                result.Changed = true;
                result.Lines.Add("🗑 📁 " + project.Name);
                break;
            }
        }
    }

    /// <summary>Reads "due" and "repeat" (when present) into the item.</summary>
    private static void ApplySchedule(TaskItem item, JsonObject a, int defaultHour, bool isNew) {
        bool hasDueKey = a.ContainsKey("due");
        bool hasRepeatKey = a.ContainsKey("repeat");
        if (hasRepeatKey) {
            item.Recurrence = a["repeat"] is JsonObject repeat ? ParseRepeat(repeat) : null;
        }
        if (hasDueKey) {
            if (TaskTime.TryParseLocal(Str(a, "due"), out var local, out bool hasTime)) {
                if (!hasTime) {
                    local = local.Date.AddHours(defaultHour);
                }
                if (item.Recurrence != null) {
                    local = RecurrenceMath.FirstOnOrAfter(item.Recurrence, local);
                }
                item.Due = TaskTime.FromLocal(local);
                item.HasTime = hasTime;
            } else {
                item.Due = null;
                item.HasTime = false;
            }
        }
        if (item.Recurrence != null && item.Due == null) {
            // A repeat without a start: today at the default hour, or the next match after now.
            var start = TaskTime.LocalNow.Date.AddHours(defaultHour);
            var first = RecurrenceMath.FirstOnOrAfter(item.Recurrence, start);
            if (first <= TaskTime.LocalNow) {
                first = RecurrenceMath.Next(item.Recurrence, first, TaskTime.LocalNow) ?? first;
            }
            item.Due = TaskTime.FromLocal(first);
            item.HasTime = false;
        }
        if (!isNew && hasDueKey && item.Due != null && item.Completed && item.Due > TaskTime.UtcNow) {
            item.Completed = false;
            item.CompletedAt = null;
        }
    }

    public static Recurrence? ParseRepeat(JsonObject o) {
        var rule = new Recurrence();
        string frequency = (Str(o, "frequency") ?? Str(o, "freq") ?? "").ToLowerInvariant();
        rule.Frequency = frequency switch {
            "weekly" or "week" => RecurrenceFrequency.Weekly,
            "monthly" or "month" => RecurrenceFrequency.Monthly,
            "yearly" or "year" or "annually" => RecurrenceFrequency.Yearly,
            "daily" or "day" => RecurrenceFrequency.Daily,
            _ => RecurrenceFrequency.Daily,
        };
        rule.Interval = Math.Clamp(Int(o, "interval") ?? 1, 1, 365);
        foreach (var day in Strings(o["days"])) {
            if (RecurrenceMath.TryParseDay(day, out var d) && !rule.Days.Contains(d)) {
                rule.Days.Add(d);
            }
        }
        if (rule.Days.Count > 0 && rule.Frequency == RecurrenceFrequency.Daily && rule.Days.Count < 7) {
            rule.Frequency = RecurrenceFrequency.Weekly;
        }
        if (TaskTime.TryParseLocal(Str(o, "until"), out var until, out _)) {
            rule.Until = TaskTime.FromLocal(until.Date);
        }
        return rule;
    }

    private static JsonObject RepeatToJson(Recurrence r) {
        var o = new JsonObject {
            ["frequency"] = r.Frequency.ToString().ToLowerInvariant(),
            ["interval"] = r.Interval,
        };
        if (r.Days.Count > 0) {
            o["days"] = new JsonArray(r.Days.Select(d => (JsonNode?)JsonValue.Create(d.ToString()[..3].ToLowerInvariant())).ToArray());
        }
        if (r.Until is { } until) {
            o["until"] = TaskTime.ToLocal(until).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return o;
    }

    private static List<string> ResolveProjects(TaskChange change, JsonNode? ids, JsonNode? newNames, AssistantResult result) {
        var list = new List<string>();
        foreach (var reference in Strings(ids)) {
            var project = FindProject(change, reference);
            if (project != null && !list.Contains(project.Id)) {
                list.Add(project.Id);
            }
        }
        foreach (var name in Strings(newNames)) {
            var project = FindProjectByName(change, name);
            if (project == null && name.Trim().Length > 0) {
                project = change.AddProject(name);
                result.Lines.Add("📁 " + Loc.F("New project: {0}", project.Name));
            }
            if (project != null && !list.Contains(project.Id)) {
                list.Add(project.Id);
            }
        }
        return list;
    }

    /// <summary>By id, else by name (models sometimes answer with the name).</summary>
    private static Project? FindProject(TaskChange change, string? reference) =>
        change.FindProject(reference) ?? (reference == null ? null : FindProjectByName(change, reference));

    private static Project? FindProjectByName(TaskChange change, string name) {
        string n = WorkspaceMode.Normalize(name).Trim();
        if (n.Length == 0) {
            return null;
        }
        return change.Projects.FirstOrDefault(p => WorkspaceMode.Normalize(p.Name).Trim() == n)
            ?? change.Projects.FirstOrDefault(p => p.Keywords.Any(k => WorkspaceMode.Normalize(k).Trim() == n));
    }

    /// <summary>"💡 Title · Joker, Site" or "📌 Title — Thu 17:00 · every week".</summary>
    public static string Line(TaskItem item, IEnumerable<Project> projects) {
        var sb = new StringBuilder();
        sb.Append(item.Kind == ItemKind.Task ? "📌 " : "💡 ");
        sb.Append(item.Title);
        if (item.Due is { } due) {
            sb.Append(" — ").Append(TaskTime.Describe(due, item.HasTime));
        }
        if (item.Recurrence != null) {
            sb.Append(" · 🔁 ").Append(RecurrenceMath.Describe(item.Recurrence));
        }
        var names = projects.Where(p => item.ProjectIds.Contains(p.Id)).Select(p => p.Name).ToList();
        sb.Append(" · ").Append(names.Count > 0 ? "📁 " + string.Join(", ", names) : Loc.T("Inbox"));
        return sb.ToString();
    }

    private static string Describe(List<JsonObject> actions, TaskStore store) {
        if (actions.Count == 1) {
            var a = actions[0];
            string? title = Str(a, "title") ?? (Str(a, "id") is { } id ? store.Find(id)?.Title : null);
            string what = Type(a) switch {
                "add" => Loc.T("Add"),
                "update" => Loc.T("Edit"),
                "complete" => Loc.T("Complete"),
                "delete" => Loc.T("Delete"),
                "snooze" => Loc.T("Snooze"),
                _ => Type(a),
            };
            return title != null ? what + " \"" + Short(title) + "\"" : what;
        }
        return Loc.F("{0} changes by the assistant", actions.Count);
    }

    internal static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";

    private static string Type(JsonObject a) => (Str(a, "type") ?? "").Trim().ToLowerInvariant();

    private static string? Str(JsonObject o, string key) {
        if (o[key] is not JsonValue value) {
            return null;
        }
        if (value.TryGetValue(out string? s)) {
            return s;
        }
        return value.ToJsonString().Trim('"');
    }

    private static int? Int(JsonObject o, string key) {
        if (o[key] is not JsonValue value) {
            return null;
        }
        if (value.TryGetValue(out int i)) {
            return i;
        }
        if (value.TryGetValue(out double d)) {
            return (int)Math.Round(d);
        }
        return value.TryGetValue(out string? s) && int.TryParse(s, out int parsed) ? parsed : null;
    }

    private static IEnumerable<string> Strings(JsonNode? node) {
        if (node is JsonArray array) {
            foreach (var entry in array) {
                if (entry is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)) {
                    yield return s.Trim();
                }
            }
        } else if (node is JsonValue single && single.TryGetValue(out string? one) && !string.IsNullOrWhiteSpace(one)) {
            yield return one.Trim();
        }
    }
}
