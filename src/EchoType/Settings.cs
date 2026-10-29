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

    /// <summary>
    /// Legacy single dictation key. Kept in sync with the last key of
    /// <see cref="HotkeyVks"/> so older config files keep working.
    /// </summary>
    [JsonPropertyName("hotkeyVk")]
    public int HotkeyVk { get; set; } = 0xA3; // VK_RCONTROL

    /// <summary>
    /// One to three keys (e.g. Right Ctrl, or Ctrl+Space) that start dictation.
    /// Empty falls back to <see cref="HotkeyVk"/>.
    /// </summary>
    [JsonPropertyName("hotkeyVks")]
    public List<int> HotkeyVks { get; set; } = [];

    [JsonIgnore]
    public HotkeyChord DictationChord => HotkeyChord.FromVks(HotkeyVks, HotkeyVk);

    public void SetDictationChord(HotkeyChord chord) {
        HotkeyVks = chord.ToIntArray().ToList();
        HotkeyVk = chord.LegacyVk;
    }

    /// <summary>
    /// Optional second dictation shortcut (e.g. Ctrl+Space alongside Right Ctrl).
    /// Either one starts the same dictation. Empty = none.
    /// </summary>
    [JsonPropertyName("secondHotkeyVks")]
    public List<int> SecondHotkeyVks { get; set; } = [];

    [JsonIgnore]
    public HotkeyChord SecondDictationChord => HotkeyChord.FromVks(SecondHotkeyVks);

    public void SetSecondDictationChord(HotkeyChord chord) =>
        SecondHotkeyVks = chord.ToIntArray().ToList();

    /// <summary>Every non-empty dictation shortcut, primary first.</summary>
    [JsonIgnore]
    public IEnumerable<HotkeyChord> DictationChords {
        get {
            yield return DictationChord;
            var second = SecondDictationChord;
            if (!second.IsEmpty && !second.Equals(DictationChord)) {
                yield return second;
            }
        }
    }

    public bool IsDictationChord(HotkeyChord chord) =>
        !chord.IsEmpty && DictationChords.Any(d => d.Equals(chord));

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
    /// When the site ends the recording on its own (silence detection, max clip length) while
    /// the key is still held (or not yet pressed again in toggle mode), keep that piece, restart
    /// the page's dictation, and paste every piece joined as one transcript.
    /// </summary>
    [JsonPropertyName("longDictation")]
    public bool LongDictation { get; set; }

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
    /// translate the transcript with OpenAI (when a key is set) or Google Translate and paste it.
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

    /// <summary>
    /// One to three keys that translate the text selected in any app into
    /// <see cref="TranslateSelectionTargetLanguage"/> and show it in a popup, keeping the original
    /// line breaks, blank lines, indentation and spacing. Default Ctrl+Shift+Y; empty = off.
    /// </summary>
    [JsonPropertyName("translateSelectionVks")]
    public List<int> TranslateSelectionVks { get; set; } = [0x11, 0x10, 0x59];

    [JsonIgnore]
    public HotkeyChord TranslateSelectionChord => HotkeyChord.FromVks(TranslateSelectionVks);

    public void SetTranslateSelectionChord(HotkeyChord chord) {
        TranslateSelectionVks = chord.ToIntArray().ToList();
    }

    /// <summary>
    /// One to three keys that open the quick "add word replacement" popup, pre-filled with
    /// the word selected in any app. Default Ctrl+Shift+U; empty = off.
    /// </summary>
    [JsonPropertyName("addWordReplacementVks")]
    public List<int> AddWordReplacementVks { get; set; } = [0x11, 0x10, 0x55];

    [JsonIgnore]
    public HotkeyChord AddWordReplacementChord => HotkeyChord.FromVks(AddWordReplacementVks);

    public void SetAddWordReplacementChord(HotkeyChord chord) {
        AddWordReplacementVks = chord.ToIntArray().ToList();
    }

    /// <summary>
    /// One to three keys that open the quick-add bar (type an idea, Enter → Inbox) from any app.
    /// Default Ctrl+Alt+Space; empty = off.
    /// </summary>
    [JsonPropertyName("quickAddVks")]
    public List<int> QuickAddVks { get; set; } = [0x11, 0x12, 0x20];

    [JsonIgnore]
    public HotkeyChord QuickAddChord => HotkeyChord.FromVks(QuickAddVks);

    public void SetQuickAddChord(HotkeyChord chord) {
        QuickAddVks = chord.ToIntArray().ToList();
    }

    /// <summary>
    /// One to three keys you hold (or press, in press-to-start/stop mode) to talk to the tasks
    /// assistant: "add a task for Thursday…", "delete it", "undo". Default Ctrl+Alt+V; empty = off.
    /// </summary>
    [JsonPropertyName("taskVoiceVks")]
    public List<int> TaskVoiceVks { get; set; } = [0x11, 0x12, 0x56];

    [JsonIgnore]
    public HotkeyChord TaskVoiceChord => HotkeyChord.FromVks(TaskVoiceVks);

    public void SetTaskVoiceChord(HotkeyChord chord) {
        TaskVoiceVks = chord.ToIntArray().ToList();
    }

    /// <summary>
    /// Google Gemini API key: transcribes Telegram voice notes and lets the tasks assistant read
    /// typed or Telegram text. Without it, spoken commands use the cleaning model's window and
    /// Telegram text is saved as plain ideas. The GEMINI_API_KEY environment variable also works.
    /// </summary>
    [JsonPropertyName("geminiApiKey")]
    public string GeminiApiKey { get; set; } = "";

    /// <summary>Gemini model id. Empty = <c>gemini-flash-latest</c>.</summary>
    [JsonPropertyName("geminiModel")]
    public string GeminiModel { get; set; } = "";

    /// <summary>
    /// How the tasks assistant understands what you say: "auto" (Gemini when a key is set, otherwise
    /// the cleaning model's window), "gemini" or "web".
    /// </summary>
    [JsonPropertyName("taskAssistantEngine")]
    public string TaskAssistantEngine { get; set; } = "auto";

    /// <summary>Telegram bot token from @BotFather. Empty = Telegram sync off.</summary>
    [JsonPropertyName("telegramBotToken")]
    public string TelegramBotToken { get; set; } = "";

    /// <summary>The only chat the bot listens to and sends reminders to.</summary>
    [JsonPropertyName("telegramChatId")]
    public string TelegramChatId { get; set; } = "";

    [JsonPropertyName("telegramEnabled")]
    public bool TelegramEnabled { get; set; } = true;

    /// <summary>Send each due reminder to Telegram as a poll (Done / Snooze / Stop / Delete).</summary>
    [JsonPropertyName("telegramReminders")]
    public bool TelegramReminders { get; set; } = true;

    /// <summary>IANA or Windows time zone id for due dates. Default Asia/Baghdad.</summary>
    [JsonPropertyName("taskTimeZone")]
    public string TaskTimeZone { get; set; } = "Asia/Baghdad";

    /// <summary>When a task has a day but no time, its reminder fires at this hour (0–23).</summary>
    [JsonPropertyName("defaultReminderHour")]
    public int DefaultReminderHour { get; set; } = 9;

    /// <summary>An unanswered reminder chimes again every this many minutes. 0 = once.</summary>
    [JsonPropertyName("reminderRepeatMinutes")]
    public int ReminderRepeatMinutes { get; set; } = 5;

    /// <summary>Google Translate target language code for <see cref="TranslateVk"/> (e.g. "en", "ar").</summary>
    [JsonPropertyName("translateTargetLanguage")]
    public string TranslateTargetLanguage { get; set; } = "en";

    /// <summary>
    /// Target language code for the Translate selection shortcut, separate from
    /// <see cref="TranslateTargetLanguage"/> (e.g. speech → "en", selected text → "ar").
    /// Empty (older configs) = same as <see cref="TranslateTargetLanguage"/>.
    /// </summary>
    [JsonPropertyName("translateSelectionTargetLanguage")]
    public string TranslateSelectionTargetLanguage { get; set; } = "";

    /// <summary>
    /// OpenAI API key for the Translate shortcut. When set (here or in the OPENAI_API_KEY
    /// environment variable) translation goes through OpenAI, which handles mixed-language
    /// text; Google Translate stays the fallback. Empty = Google Translate only.
    /// </summary>
    [JsonPropertyName("openAiApiKey")]
    public string OpenAiApiKey { get; set; } = "";

    /// <summary>OpenAI model used for translation. Empty = <c>gpt-4o-mini</c>.</summary>
    [JsonPropertyName("openAiTranslationModel")]
    public string OpenAiTranslationModel { get; set; } = "";

    /// <summary>
    /// Interface language: "en", "ar", or empty to follow the Windows display language.
    /// Applied at startup; changing it from the window restarts EchoType.
    /// </summary>
    [JsonPropertyName("uiLanguage")]
    public string UiLanguage { get; set; } = "";

    /// <summary>
    /// When true (default), Ask model replies also open in a popup with Copy buttons
    /// (text and images), in addition to being pasted or copied.
    /// </summary>
    [JsonPropertyName("showAskReplyWindow")]
    public bool ShowAskReplyWindow { get; set; } = true;

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

    /// <summary>"chatgpt", "gemini" or "claude". Runs Auto Clean, custom commands and selection rewrites (Ask model has its own <see cref="AskProvider"/>).</summary>
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

    /// <summary>
    /// "chatgpt", "gemini" or "claude". Answers the Ask model shortcut in its own window
    /// (shared with the transcription or cleaning window when it is the same model).
    /// Missing from older configs → the transcription model, which used to answer Ask model.
    /// </summary>
    [JsonPropertyName("askProvider")]
    public string AskProviderName { get; set; } = "";

    [JsonIgnore]
    public TranscriptionProvider AskProvider {
        get => AskProviderName.ToLowerInvariant() switch {
            "gemini" => TranscriptionProvider.Gemini,
            "claude" => TranscriptionProvider.Claude,
            _ => TranscriptionProvider.ChatGpt,
        };
        set => AskProviderName = value switch {
            TranscriptionProvider.Gemini => "gemini",
            TranscriptionProvider.Claude => "claude",
            _ => "chatgpt",
        };
    }

    /// <summary>
    /// Manual word fixes applied to every transcript (and to Auto Clean's reply) before it is
    /// used, e.g. to keep dialect words. Empty = off.
    /// </summary>
    [JsonPropertyName("wordReplacements")]
    public List<WordReplacement> WordReplacements { get; set; } = [];

    /// <summary>
    /// Typed abbreviations expanded in any app when followed by Space (وع → وعليكم السلام…).
    /// Missing from older configs → the shipped examples.
    /// </summary>
    [JsonPropertyName("textShortcuts")]
    public List<TextShortcut> TextShortcuts { get; set; } = TextShortcut.Defaults();

    /// <summary>Master switch for <see cref="TextShortcuts"/>.</summary>
    [JsonPropertyName("textShortcutsEnabled")]
    public bool TextShortcutsEnabled { get; set; } = true;

    /// <summary>
    /// Spoken workspace switches: a short dictation such as "coding mode" presses the mode's
    /// shortcut instead of pasting. Missing from older configs → the four default modes.
    /// </summary>
    [JsonPropertyName("workspaceModes")]
    public List<WorkspaceMode> WorkspaceModes { get; set; } = WorkspaceMode.Defaults();

    /// <summary>
    /// Per-app overrides for Auto Enter, Auto Clean, the cleaning prompt and the Translate
    /// language, matched on the process name of the window dictation started in. Empty = off.
    /// </summary>
    [JsonPropertyName("appProfiles")]
    public List<AppProfile> AppProfiles { get; set; } = [];

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
                defaults.TranslateSelectionTargetLanguage = defaults.TranslateTargetLanguage;
                defaults.Save();
                return defaults;
            }
            var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
            loaded.CustomCommands ??= [];
            loaded.GeminiCustomCommands ??= [];
            loaded.HotkeyVks ??= [];
            loaded.SecondHotkeyVks ??= [];
            loaded.TranslateVks ??= [];
            loaded.TranslateSelectionVks ??= [];
            loaded.AddWordReplacementVks ??= [];
            loaded.QuickAddVks ??= [];
            loaded.TaskVoiceVks ??= [];
            loaded.GeminiApiKey ??= "";
            loaded.GeminiModel ??= "";
            loaded.TaskAssistantEngine ??= "auto";
            loaded.TelegramBotToken ??= "";
            loaded.TelegramChatId ??= "";
            if (string.IsNullOrWhiteSpace(loaded.TaskTimeZone)) {
                loaded.TaskTimeZone = "Asia/Baghdad";
            }
            loaded.DefaultReminderHour = Math.Clamp(loaded.DefaultReminderHour, 0, 23);
            loaded.ReminderRepeatMinutes = Math.Clamp(loaded.ReminderRepeatMinutes, 0, 240);
            loaded.OpenAiApiKey ??= "";
            loaded.OpenAiTranslationModel ??= "";
            loaded.WordReplacements ??= [];
            loaded.TextShortcuts ??= [];
            loaded.TextShortcuts.RemoveAll(s => s == null);
            foreach (var shortcut in loaded.TextShortcuts) {
                shortcut.Trigger ??= "";
                shortcut.Text ??= "";
            }
            loaded.WorkspaceModes ??= [];
            foreach (var mode in loaded.WorkspaceModes) {
                mode.Keywords ??= [];
            }
            loaded.AppProfiles ??= [];
            loaded.AppProfiles.RemoveAll(p => p == null);
            foreach (var profile in loaded.AppProfiles) {
                profile.Normalize();
            }
            if (string.IsNullOrWhiteSpace(loaded.TranslateTargetLanguage)) {
                loaded.TranslateTargetLanguage = "en";
            }
            if (string.IsNullOrWhiteSpace(loaded.TranslateSelectionTargetLanguage)) {
                loaded.TranslateSelectionTargetLanguage = loaded.TranslateTargetLanguage;
            }
            if (string.IsNullOrWhiteSpace(loaded.TranscriptionProviderName)) {
                loaded.TranscriptionProviderName = "chatgpt";
            }
            if (string.IsNullOrWhiteSpace(loaded.CleanProviderName)) {
                loaded.CleanProviderName = "chatgpt";
            }
            if (string.IsNullOrWhiteSpace(loaded.AskProviderName)) {
                loaded.AskProviderName = loaded.TranscriptionProviderName;
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
