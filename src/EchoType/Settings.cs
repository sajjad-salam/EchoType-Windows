using System.Text.Json;
using System.Text.Json.Serialization;
using EchoType.Hotkey;

namespace EchoType;

/// <summary>
/// Settings persisted to %APPDATA%\EchoType\config.json (Settings.swift analog, minus
/// the keys that only back UI that doesn't exist in the MVP). Recording mode, shortcuts,
/// custom commands and the dictation key are edited from the main window (or tray) and saved immediately.
/// </summary>
internal sealed class Settings {

    [JsonPropertyName("hotkeyVk")]
    public int HotkeyVk { get; set; } = 0xA3; // VK_RCONTROL

    [JsonPropertyName("keepTranscriptOnClipboard")]
    public bool KeepTranscriptOnClipboard { get; set; } = true;

    [JsonPropertyName("pressEnterAfterPaste")]
    public bool PressEnterAfterPaste { get; set; } = true;

    /// <summary>Virtual-key that toggles <see cref="PressEnterAfterPaste"/>. 0 = none.</summary>
    [JsonPropertyName("pressEnterToggleVk")]
    public int PressEnterToggleVk { get; set; }

    /// <summary>
    /// When dictation starts, pause other apps that expose play/pause (YouTube,
    /// Spotify, …) and mute remaining playback so it doesn't bleed into the
    /// microphone. Restored when listening ends.
    /// </summary>
    [JsonPropertyName("muteOtherAppsWhileDictating")]
    public bool MuteOtherAppsWhileDictating { get; set; } = true;

    /// <summary>
    /// When false (default), hold a recording shortcut to talk and release to stop.
    /// When true, press once to start and press the same key again to stop.
    /// Applies to dictation, selection rewrite, Ask model, and custom commands.
    /// </summary>
    /// <summary>Launch EchoType automatically when the user signs in to Windows.</summary>
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; } = true;

    [JsonPropertyName("toggleRecording")]
    public bool ToggleRecording { get; set; }

    /// <summary>"Hold" or "Press", matching the current recording mode.</summary>
    [JsonIgnore]
    public string RecordingVerb => ToggleRecording ? "Press" : "Hold";

    /// <summary>Tap key that switches to ChatGPT. 0 = none.</summary>
    [JsonPropertyName("chatgptSwitchVk")]
    public int ChatGptSwitchVk { get; set; }

    /// <summary>Tap key that switches to Gemini. 0 = none.</summary>
    [JsonPropertyName("geminiSwitchVk")]
    public int GeminiSwitchVk { get; set; }

    /// <summary>
    /// Hold-to-talk / press-to-toggle key that sends the spoken text (and any selection) to the
    /// selected model and pastes the reply — text or image. 0 = none.
    /// </summary>
    [JsonPropertyName("askModelVk")]
    public int AskModelVk { get; set; }

    /// <summary>
    /// Legacy single key for the Translate shortcut. Kept in sync with the last key of
    /// <see cref="TranslateVks"/> so older config files keep working. 0 = none.
    /// </summary>
    [JsonPropertyName("translateVk")]
    public int TranslateVk { get; set; }

    /// <summary>
    /// One to three keys (e.g. Ctrl+Shift+T) that transcribe with the selected model, then
    /// translate the transcript with Google Translate (no model reply) and paste it.
    /// Empty falls back to <see cref="TranslateVk"/>.
    /// </summary>
    [JsonPropertyName("translateVks")]
    public List<int> TranslateVks { get; set; } = [];

    [JsonIgnore]
    public HotkeyChord TranslateChord => HotkeyChord.FromVks(TranslateVks, TranslateVk);

    public void SetTranslateChord(HotkeyChord chord) {
        TranslateVks = chord.ToIntArray().ToList();
        TranslateVk = chord.LegacyVk;
    }

    /// <summary>Google Translate target language code for <see cref="TranslateVk"/> (e.g. "en", "ar").</summary>
    [JsonPropertyName("translateTargetLanguage")]
    public string TranslateTargetLanguage { get; set; } = "en";

    /// <summary>
    /// Tap key that opens the ChatGPT/Gemini window (same as tray Login). 0 = none.
    /// </summary>
    [JsonPropertyName("openModelWindowVk")]
    public int OpenModelWindowVk { get; set; }

    /// <summary>"chatgpt" or "gemini". Unknown values fall back to ChatGPT.</summary>
    [JsonPropertyName("transcriptionProvider")]
    public string TranscriptionProviderName { get; set; } = "chatgpt";

    [JsonPropertyName("customCommands")]
    public List<CustomCommand> CustomCommands { get; set; } = [];

    /// <summary>Legacy per-model list. Merged into <see cref="CustomCommands"/> on load.</summary>
    [JsonPropertyName("geminiCustomCommands")]
    public List<CustomCommand> GeminiCustomCommands { get; set; } = [];

    [JsonIgnore]
    public TranscriptionProvider TranscriptionProvider {
        get => TranscriptionProviderName.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            ? TranscriptionProvider.Gemini
            : TranscriptionProvider.ChatGpt;
        set => TranscriptionProviderName = value == TranscriptionProvider.Gemini ? "gemini" : "chatgpt";
    }

    /// <summary>Custom commands are global — the same list is used for ChatGPT and Gemini.</summary>
    [JsonIgnore]
    public List<CustomCommand> ActiveCommands => CustomCommands;

    /// <summary>Tap-style reserved keys (Auto Enter, model switch, model window). 0 is omitted.</summary>
    [JsonIgnore]
    public IEnumerable<int> TapHotkeyVks {
        get {
            if (PressEnterToggleVk > 0) {
                yield return PressEnterToggleVk;
            }
            if (ChatGptSwitchVk > 0) {
                yield return ChatGptSwitchVk;
            }
            if (GeminiSwitchVk > 0) {
                yield return GeminiSwitchVk;
            }
            if (OpenModelWindowVk > 0) {
                yield return OpenModelWindowVk;
            }
        }
    }

    private static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoType");

    private static string FilePath { get; } = System.IO.Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    public static Settings Load() {
        try {
            if (!File.Exists(FilePath)) {
                var defaults = new Settings();
                defaults.Save();
                return defaults;
            }
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
            loaded.CustomCommands ??= [];
            loaded.GeminiCustomCommands ??= [];
            loaded.TranslateVks ??= [];
            if (string.IsNullOrWhiteSpace(loaded.TranslateTargetLanguage)) {
                loaded.TranslateTargetLanguage = "en";
            }
            if (string.IsNullOrWhiteSpace(loaded.TranscriptionProviderName)) {
                loaded.TranscriptionProviderName = "chatgpt";
            }
            bool migrated = loaded.MergeLegacyGeminiCommands();
            foreach (var cmd in loaded.CustomCommands) {
                cmd.Normalize();
            }
            if (migrated) {
                loaded.Save();
            }
            return loaded;
        } catch (Exception ex) {
            Log.Write($"settings: failed to load ({ex.Message}) — using defaults");
            return new Settings();
        }
    }

    /// <summary>
    /// Older builds kept a separate Gemini command list. Fold those into the
    /// global list once so switching models no longer hides shortcuts.
    /// </summary>
    private bool MergeLegacyGeminiCommands() {
        if (GeminiCustomCommands.Count == 0) {
            return false;
        }
        var ids = CustomCommands.Select(c => c.Id).ToHashSet();
        foreach (var cmd in GeminiCustomCommands) {
            if (ids.Add(cmd.Id)) {
                CustomCommands.Add(cmd);
            }
        }
        GeminiCustomCommands.Clear();
        return true;
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        } catch (Exception ex) {
            Log.Write("settings: failed to save: " + ex.Message);
        }
    }
}
