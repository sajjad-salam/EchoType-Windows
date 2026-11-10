namespace EchoType.Health;

internal enum HealthLevel {
    Ok,
    Pending,
    Warning,
    Error,
}

/// <summary>
/// One line of the Health card / setup wizard: what is checked, its state in plain words,
/// and the button that fixes it (null when nothing needs doing).
/// </summary>
internal sealed record HealthItem(string Id, string Title, string Status, HealthLevel Level, string? FixLabel);
