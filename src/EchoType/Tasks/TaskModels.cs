using System.Text.Json.Serialization;

namespace EchoType.Tasks;

/// <summary>What the user captured: a loose idea, or a task with something to do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ItemKind>))]
internal enum ItemKind {
    Idea,
    Task,
}

/// <summary>
/// One idea or task. Ideas start in the Inbox; any item can be linked to several projects
/// (one item, many links — never a duplicate per project). <see cref="Due"/> is the next
/// moment to remind, stored in UTC and shown in the task time zone (Baghdad by default).
/// </summary>
internal sealed class TaskItem {

    [JsonPropertyName("id")]
    public string Id { get; set; } = NewId();

    [JsonPropertyName("kind")]
    public ItemKind Kind { get; set; } = ItemKind.Idea;

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    [JsonPropertyName("projectIds")]
    public List<string> ProjectIds { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Next reminder moment (UTC). Null = no date.</summary>
    [JsonPropertyName("due")]
    public DateTimeOffset? Due { get; set; }

    /// <summary>False when only a day was given; the reminder then fires at the default hour.</summary>
    [JsonPropertyName("hasTime")]
    public bool HasTime { get; set; }

    [JsonPropertyName("recurrence")]
    public Recurrence? Recurrence { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Where it came from: app, quick, voice, telegram, assistant.</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "app";

    [JsonIgnore]
    public bool IsOpen => !Completed;

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

internal sealed class Project {

    [JsonPropertyName("id")]
    public string Id { get; set; } = TaskItem.NewId();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Extra words that link an idea to this project (other spellings, nicknames).</summary>
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

[JsonConverter(typeof(JsonStringEnumConverter<RecurrenceFrequency>))]
internal enum RecurrenceFrequency {
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// Repeat rule. Weekly with several <see cref="Days"/> covers "three times a week"
/// (e.g. Sunday, Tuesday, Thursday).
/// </summary>
internal sealed class Recurrence {

    [JsonPropertyName("frequency")]
    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Daily;

    /// <summary>Every N days / weeks / months / years.</summary>
    [JsonPropertyName("interval")]
    public int Interval { get; set; } = 1;

    /// <summary>Weekly only: the weekdays it falls on. Empty = the due date's weekday.</summary>
    [JsonPropertyName("days")]
    public List<DayOfWeek> Days { get; set; } = [];

    /// <summary>Last day (local) an occurrence may fall on. Null = forever.</summary>
    [JsonPropertyName("until")]
    public DateTimeOffset? Until { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<NoticeState>))]
internal enum NoticeState {
    /// <summary>On screen (and sent to Telegram) until the user acts.</summary>
    Active,
    Snoozed,
    Done,
    /// <summary>Reminder stopped; the task itself is untouched.</summary>
    Dismissed,
    /// <summary>The task was deleted.</summary>
    Deleted,
}

/// <summary>One fired reminder, as listed in the notification center.</summary>
internal sealed class Notice {

    [JsonPropertyName("id")]
    public string Id { get; set; } = TaskItem.NewId();

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    /// <summary>Title at the time it fired, so history still reads well after edits.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>The occurrence (task due) this reminder is for.</summary>
    [JsonPropertyName("occurrence")]
    public DateTimeOffset Occurrence { get; set; }

    [JsonPropertyName("firedAt")]
    public DateTimeOffset FiredAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("state")]
    public NoticeState State { get; set; } = NoticeState.Active;

    [JsonPropertyName("snoozeUntil")]
    public DateTimeOffset? SnoozeUntil { get; set; }

    [JsonPropertyName("resolvedAt")]
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>"app" or "telegram": where it was resolved.</summary>
    [JsonPropertyName("resolvedFrom")]
    public string ResolvedFrom { get; set; } = "";

    [JsonPropertyName("telegramPollId")]
    public string? TelegramPollId { get; set; }

    [JsonPropertyName("telegramMessageId")]
    public long? TelegramMessageId { get; set; }

    /// <summary>Fired after its time because EchoType wasn't running.</summary>
    [JsonPropertyName("late")]
    public bool Late { get; set; }

    [JsonIgnore]
    public bool IsOpen => State is NoticeState.Active or NoticeState.Snoozed;
}

/// <summary>What to do with a reminder (toast buttons, Telegram poll options, voice).</summary>
internal enum NoticeAction {
    Done,
    Snooze10,
    Snooze60,
    SnoozeTomorrow,
    Stop,
    Delete,
}
