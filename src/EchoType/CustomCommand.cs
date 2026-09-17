using System.Text.Json.Serialization;
using EchoType.Hotkey;
using EchoType.Input;

namespace EchoType;

/// <summary>
/// One action inside a custom command. A command can be a single button
/// (legacy) or a group of buttons shown after dictation.
/// </summary>
internal sealed class CommandButton {

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "Action" : Name.Trim();

    public CommandButton Clone() => new() {
        Id = Id,
        Name = Name,
        Prompt = Prompt,
    };
}

/// <summary>
/// A recording shortcut that sends the transcript plus a user prompt to the
/// selected model (ChatGPT or Gemini) and pastes the reply into the focused field.
/// One command can contain several action buttons; after dictation EchoType
/// asks which button to run when there is more than one.
/// </summary>
internal sealed class CustomCommand {

    public const int MaxButtons = 8;

    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("hotkeyVk")]
    public int HotkeyVk { get; set; }

    /// <summary>
    /// Up to three keys for this command’s shortcut. Empty falls back to
    /// <see cref="HotkeyVk"/> so older config files keep working.
    /// </summary>
    [JsonPropertyName("hotkeyVks")]
    public List<int> HotkeyVks { get; set; } = [];

    [JsonIgnore]
    public HotkeyChord Chord => HotkeyChord.FromVks(HotkeyVks, HotkeyVk);

    public void SetChord(HotkeyChord chord) {
        HotkeyVks = chord.ToIntArray().ToList();
        HotkeyVk = chord.LegacyVk;
    }

    /// <summary>Legacy single-prompt field. Kept in sync with the first button.</summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("buttons")]
    public List<CommandButton> Buttons { get; set; } = [];

    [JsonIgnore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Name) ? "Custom command" : Name.Trim();

    [JsonIgnore]
    public IReadOnlyList<CommandButton> ResolvedButtons {
        get {
            Normalize();
            return Buttons;
        }
    }

    public CustomCommand Clone() {
        Normalize();
        return new() {
            Id = Id,
            Name = Name,
            HotkeyVk = HotkeyVk,
            HotkeyVks = [.. HotkeyVks],
            Prompt = Prompt,
            Buttons = Buttons.Select(b => b.Clone()).ToList(),
        };
    }

    public void CopyFrom(CustomCommand other) {
        other.Normalize();
        Name = other.Name;
        HotkeyVk = other.HotkeyVk;
        HotkeyVks = [.. other.HotkeyVks];
        Prompt = other.Prompt;
        Buttons = other.Buttons.Select(b => b.Clone()).ToList();
    }

    /// <summary>
    /// Folds the legacy <see cref="Prompt"/> into <see cref="Buttons"/> so
    /// older config files keep working.
    /// </summary>
    public void Normalize() {
        Buttons ??= [];
        HotkeyVks ??= [];
        HotkeyVks = HotkeyVks.Where(vk => vk > 0).Take(HotkeyChord.MaxKeys).ToList();
        if (HotkeyVks.Count == 0 && HotkeyVk > 0) {
            HotkeyVks.Add(HotkeyVk);
        }
        if (HotkeyVks.Count > 0) {
            var chord = HotkeyChord.FromVks(HotkeyVks, HotkeyVk);
            HotkeyVks = chord.ToIntArray().ToList();
            HotkeyVk = chord.LegacyVk;
        }
        if (Buttons.Count == 0 && !string.IsNullOrWhiteSpace(Prompt)) {
            Buttons.Add(new CommandButton { Prompt = Prompt });
        }
        foreach (var button in Buttons) {
            button.Prompt ??= "";
            button.Name ??= "";
        }
        if (Buttons.Count > 0) {
            Prompt = Buttons[0].Prompt ?? "";
        }
    }

    /// <summary>
    /// Builds the model message. <c>{transcript}</c> is replaced when present;
    /// otherwise the spoken text is appended after the prompt. Selected text
    /// from the focused field is appended (or substituted for <c>{selection}</c>).
    /// </summary>
    public string BuildMessage(string transcript, string? selectedText = null, CommandButton? button = null) {
        Normalize();
        string spoken = transcript;
        string prompt = button?.Prompt ?? Prompt ?? "";
        bool hasSelectionPlaceholder = !string.IsNullOrEmpty(selectedText)
            && prompt.Contains("{selection}", StringComparison.OrdinalIgnoreCase);
        if (hasSelectionPlaceholder) {
            prompt = prompt.Replace("{selection}", selectedText, StringComparison.OrdinalIgnoreCase);
        } else if (!string.IsNullOrEmpty(selectedText)) {
            spoken = SelectionCapture.BuildAskModelMessage(selectedText, transcript);
        }

        if (prompt.Contains("{transcript}", StringComparison.OrdinalIgnoreCase)) {
            return prompt.Replace("{transcript}", spoken, StringComparison.OrdinalIgnoreCase);
        }
        prompt = prompt.TrimEnd();
        if (prompt.Length == 0) {
            return spoken;
        }
        return prompt + "\n\n" + spoken;
    }
}
