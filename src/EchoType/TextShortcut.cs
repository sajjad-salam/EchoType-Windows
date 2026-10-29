using System.Text.Json.Serialization;

namespace EchoType;

/// <summary>
/// A typed abbreviation that expands in any app: type the trigger, press Space, and the
/// trigger is replaced with the text (e.g. وع → وعليكم السلام ورحمة الله وبركاته).
/// </summary>
internal sealed class TextShortcut {

    public const int MaxTriggerLength = 32;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    /// <summary>Shipped examples for configs that have never saved any shortcuts.</summary>
    public static List<TextShortcut> Defaults() => [
        new() { Trigger = "وع", Text = "وعليكم السلام ورحمة الله وبركاته" },
        new() { Trigger = "سع", Text = "السلام عليكم ورحمة الله وبركاته" },
    ];

    /// <summary>A trigger is one word: no spaces or line breaks, at most 32 characters.</summary>
    public static bool IsValidTrigger(string trigger) =>
        trigger.Length is > 0 and <= MaxTriggerLength &&!trigger.Any(char.IsWhiteSpace);
}
