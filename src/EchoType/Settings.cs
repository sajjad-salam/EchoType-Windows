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

    /// <summary>Virtual-key that toggles <see cref="AutoClean"/>. 0 = none.</summary>
    [JsonPropertyName("autoCleanToggleVk")]
    public int AutoCleanToggleVk { get; set; }

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

    /// <summary>Tap key that switches to Claude. 0 = none.</summary>
    [JsonPropertyName("claudeSwitchVk")]
    public int ClaudeSwitchVk { get; set; }

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

    /// <summary>"chatgpt", "gemini" or "claude". Unknown values fall back to ChatGPT.</summary>
    [JsonPropertyName("transcriptionProvider")]
    public string TranscriptionProviderName { get; set; } = "chatgpt";

    /// <summary>
    /// When true, plain dictation is sent to the cleaning model (<see cref="CleanProvider"/>)
    /// with <see cref="AutoCleanPrompt"/> and the cleaned text is pasted instead of the raw transcript.
    /// </summary>
    [JsonPropertyName("autoClean")]
    public bool AutoClean { get; set; }

    /// <summary>"chatgpt", "gemini" or "claude". Runs Auto Clean, custom commands and selection rewrites (Ask model uses the transcription model).</summary>
    [JsonPropertyName("cleanProvider")]
    public string CleanProviderName { get; set; } = "chatgpt";

    public const string DefaultAutoCleanPrompt =
        "Clean up this dictated text. Remove filler words (um, uh, you know), repeated words and false starts; "
        + "if I corrected myself keep only the final version; fix punctuation, capitalization and obvious "
        + "speech-to-text mistakes; keep the original language, meaning and tone. "
        + "Reply with ONLY the cleaned text, no quotes, no explanation.";

    [JsonPropertyName("autoCleanPrompt")]
    public string AutoCleanPrompt { get; set; } = DefaultAutoCleanPrompt;

    [JsonIgnore]
    public TranscriptionProvider CleanProvider {
        get => CleanProviderName.ToLowerInvariant() switch {
            "gemini" => TranscriptionProvider.Gemini,
            "claude" => TranscriptionProvider.Claude,
            _ => TranscriptionProvider.ChatGpt,
        };
        set => CleanProviderName = value switch {
            TranscriptionProvider.Gemini => "gemini",
            TranscriptionProvider.Claude => "claude",
            _ => "chatgpt",
        };
    }

    [JsonPropertyName("customCommands")]
    public List<CustomCommand> CustomCommands { get; set; } = [];

    /// <summary>Legacy per-model list. Merged into <see cref="CustomCommands"/> on load.</summary>
    [JsonPropertyName("geminiCustomCommands")]
    public List<CustomCommand> GeminiCustomCommands { get; set; } = [];

    [JsonIgnore]
    public TranscriptionProvider TranscriptionProvider {
        get => TranscriptionProviderName.ToLowerInvariant() switch {
            "gemini" => TranscriptionProvider.Gemini,
            "claude" => TranscriptionProvider.Claude,
            _ => TranscriptionProvider.ChatGpt,
        };
        set => TranscriptionProviderName = value switch {
            TranscriptionProvider.Gemini => "gemini",
            TranscriptionProvider.Claude => "claude",
            _ => "chatgpt",
        };
    }

    /// <summary>Custom commands are global — the same list is used for ChatGPT, Gemini and Claude.</summary>
    [JsonIgnore]
    public List<CustomCommand> ActiveCommands => CustomCommands;

    /// <summary>Tap-style reserved keys (Auto Enter, Auto Clean, model switch, model window). 0 is omitted.</summary>
    [JsonIgnore]
    public IEnumerable<int> TapHotkeyVks {
        get {
            if (PressEnterToggleVk > 0) {
                yield return PressEnterToggleVk;
            }
            if (AutoCleanToggleVk > 0) {
                yield return AutoCleanToggleVk;
            }
            if (ChatGptSwitchVk > 0) {
                yield return ChatGptSwitchVk;
            }
            if (GeminiSwitchVk > 0) {
                yield return GeminiSwitchVk;
            }
            if (ClaudeSwitchVk > 0) {
                yield return ClaudeSwitchVk;
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
            if (string.IsNullOrWhiteSpace(loaded.CleanProviderName)) {
                loaded.CleanProviderName = "chatgpt";
            }
            if (string.IsNullOrWhiteSpace(loaded.AutoCleanPrompt)) {
                loaded.AutoCleanPrompt = DefaultAutoCleanPrompt;
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
