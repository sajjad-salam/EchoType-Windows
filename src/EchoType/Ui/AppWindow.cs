using EchoType.Health;
using EchoType.Hotkey;
using EchoType.Native;
using EchoType.Translation;

namespace EchoType.Ui;

internal enum AppShortcut {
    Dictation,
    SecondDictation,
    AskModel,
    Translate,
    TranslateSelection,
    AddWordReplacement,
    PressEnter,
    AutoClean,
    ModelWindow,
    ChatGptWindow,
    GeminiWindow,
    ClaudeWindow,
    ChatGpt,
    Gemini,
    Claude,
    PasteLast,
    ActionPalette,
}

internal interface IAppWindowHost {
    Settings Settings { get; }
    HotkeyMonitor Hotkey { get; }
    AppPhase Phase { get; }
    bool LoggedIn { get; }
    bool Online { get; }
    bool PageReady { get; }
    string ModelName { get; }
    string CleanModelName { get; }
    bool CleanWindowVisible { get; }
    void SetAutoClean(bool value);
    void SetCleanModel(TranscriptionProvider provider);
    void ToggleCleanWindow();
    void EditCleanPrompt();
    string AskModelName { get; }
    bool AskWindowVisible { get; }
    void SetAskModel(TranscriptionProvider provider);
    void ToggleAskWindow();
    void EditOpenAiKey();
    void EditWordReplacements();
    void EditTextShortcuts();
    void SetTextShortcutsEnabled(bool value);
    void EditWorkspaceModes();
    void EditAppProfiles();
    bool LoginWindowVisible { get; }
    float MicLevel { get; }
    /// <summary>Fills the live voice spectrum (0–1 per bar); false when none is available.</summary>
    bool CopyMicSpectrum(float[] dest);
    string StatusText { get; }
    /// <summary>Saves the interface language ("en" / "ar") and restarts EchoType to apply it.</summary>
    void SetUiLanguage(string code);
    void SetShowAskReplyWindow(bool value);
    event Action? UiChanged;
    void SelectModel(TranscriptionProvider provider);
    void OpenModel(TranscriptionProvider provider);
    void SetToggleRecording(bool toggle);
    void SetMuteOthers(bool value);
    void SetLongDictation(bool value);
    void SetTranslateLanguage(string code);
    void SetTranslateSelectionLanguage(string code);
    void SetPressEnterAfterPaste(bool value);
    void SetKeepTranscriptOnClipboard(bool value);
    void SetStartWithWindows(bool value);
    bool TrySetShortcut(AppShortcut shortcut, int vk);
    bool TrySetTranslateChord(HotkeyChord chord);
    bool TrySetTranslateSelectionChord(HotkeyChord chord);
    bool TrySetAddWordReplacementChord(HotkeyChord chord);
    bool TrySetDictationChord(HotkeyChord chord);
    bool TrySetSecondDictationChord(HotkeyChord chord);
    void ToggleLoginWindow();
    void OpenCommands();
    void OpenLog();
    void Quit();

    // Health card and setup wizard
    IReadOnlyList<EchoType.Health.HealthItem> Health { get; }
    void FixHealth(string id);
    void ShowSetupWizard();
    // History
    void OpenHistory();
    void SetHistoryEnabled(bool value);
    void SetHistoryLimit(int limit);
    bool TrySetPasteLastChord(HotkeyChord chord);
    // Action palette and spoken prefixes
    bool TrySetActionPaletteChord(HotkeyChord chord);
    void SetVoicePrefixes(bool value);
    // Reliability and updates
    void SetAutoFailover(bool value);
    string UpdateStatus { get; }
    bool UpdateAvailable { get; }
    bool UpdateBusy { get; }
    void CheckForUpdates();
    void InstallUpdate();
    void SkipUpdate();
    void SetCheckForUpdates(bool value);
    void SetRemoteSelectors(bool value);
    // Advanced options and settings transfer
    void SetTranslateWithChatGpt(bool value);
    void ToggleTranslateWindow();
    bool TranslateWindowVisible { get; }
    void SetProgressPillHideMs(int ms);
    void SetOpenAiTranslationModel(string model);
    void OpenConfigFolder();
    void OpenSoundsFolder();
    void ExportSettings();
    void ImportSettings();
}

/// <summary>
/// Main EchoType window — light paper in the style of Typeless (warm off-white pages,
/// white cards, ink text, black pill buttons), with every setting that used to live
/// only in the tray. A sidebar switches between short pages so each group
/// of settings fits without scrolling.
/// </summary>
internal sealed class AppWindow : Form {

    private const int SidebarWidthDip = 224;
    private const int HeaderHeightDip = 100;
    private const int NavTopDip = 96;
    private const int PagePadDip = 28;

    // Segoe Fluent Icons / MDL2 Assets code points.
    private const string GlyphHome = "";
    private const string GlyphChat = "";
    private const string GlyphGlobe = "";
    private const string GlyphKeyboard = "";
    private const string GlyphCommand = "";
    private const string GlyphSettings = "";
    private const string GlyphHistory = "\uE81C";
    private const string GlyphAdvanced = "\uE90F";

    /// <summary>Health card rows in display order; rows the host doesn't report are hidden.</summary>
    private static readonly (string Id, string Title)[] HealthIds = [
        ("mic", "Microphone"), ("webview", "WebView2 runtime"),
        ("chatgpt", "ChatGPT"), ("gemini", "Gemini"), ("claude", "Claude"),
        ("clean", "Cleaning model"), ("ask", "Ask model"), ("translate", "ChatGPT (translation)"),
    ];

    private static int _lastPage;

    private readonly IAppWindowHost _host;
    private readonly Panel _sidebar;
    private readonly Panel _header;
    private readonly Panel _scrollHost;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<Control, int> _rowHeights = [];
    private readonly HudCard _statusCard;
    private readonly HudButton _loginButton;
    private readonly HudSegmented _modelSeg;
    private readonly HudSegmented _modeSeg;
    private readonly HudHotkeyRow _dictationKey;
    private readonly HudHotkeyRow _secondDictationKey;
    private readonly HudToggleRow _cleanToggle;
    private readonly HudSegmented _cleanSeg;
    private readonly HudHotkeyRow _cleanKey;
    private readonly HudButton _cleanWindowButton;
    private readonly HudSegmented _askSeg;
    private readonly HudHotkeyRow _askModelKey;
    private readonly HudToggleRow _askPopupToggle;
    private readonly HudButton _askWindowButton;
    private readonly HudHotkeyRow _translateKey;
    private readonly HudHotkeyRow _translateSelectionKey;
    private readonly HudHotkeyRow _addWordKey;
    private readonly ComboBox _translateLanguage;
    private readonly ComboBox _translateSelectionLanguage;
    private readonly HudSettingRow _openAiRow;
    private readonly HudButton _openAiButton;
    private readonly HudHotkeyRow _chatgptKey;
    private readonly HudHotkeyRow _geminiKey;
    private readonly HudHotkeyRow _claudeKey;
    private readonly HudHotkeyRow _enterKey;
    private readonly HudHotkeyRow _windowKey;
    private readonly HudHotkeyRow _chatgptWindowKey;
    private readonly HudHotkeyRow _geminiWindowKey;
    private readonly HudHotkeyRow _claudeWindowKey;
    private readonly HudCommandList _commands;
    private readonly HudToggleRow _muteToggle;
    private readonly HudToggleRow _longDictationToggle;
    private readonly HudToggleRow _textShortcutsToggle;
    private readonly HudSettingRow _textShortcutsRow;
    private readonly HudToggleRow _enterToggle;
    private readonly HudToggleRow _clipboardToggle;
    private readonly HudToggleRow _startupToggle;
    private readonly HudSegmented _languageSeg;
    private readonly Dictionary<string, HudSettingRow> _healthRows = [];
    private readonly Dictionary<string, HudButton> _healthButtons = [];
    /// <summary>Rows currently left out of their card's layout (e.g. health checks that don't apply).</summary>
    private readonly HashSet<Control> _collapsed = [];
    private readonly System.Windows.Forms.Timer _healthTick;
    private readonly HudToggleRow _historyToggle;
    private readonly NumericUpDown _historyLimit;
    private readonly HudHotkeyRow _pasteLastKey;
    private readonly HudHotkeyRow _paletteKey;
    private readonly HudToggleRow _voicePrefixToggle;
    private readonly HudCommandList _allShortcuts;
    private readonly HudToggleRow _chatGptTranslateToggle;
    private readonly HudButton _translateWindowButton;
    private readonly HudSettingRow _updateRow;
    private readonly HudButton _updateButton;
    private readonly HudSettingRow _skipUpdateRow;
    private readonly HudToggleRow _updatesToggle;
    private readonly HudToggleRow _remoteSelectorsToggle;
    private readonly HudToggleRow _failoverToggle;
    private readonly NumericUpDown _pillMs;
    private readonly TextBox _openAiModel;
    private readonly TextBox _search;
    private readonly ListBox _searchResults;
    private readonly List<SearchEntry> _searchIndex = [];
    private readonly Dictionary<HudCard, int> _cardPage = [];
    private List<SearchEntry> _searchShown = [];

    /// <summary>A searchable setting: its words, the page it lives on and the row to scroll to.</summary>
    private sealed record SearchEntry(string Title, string? Description, int Page, Control Row);
    private readonly string _version;
    private readonly float[] _spectrum = new float[24];
    private readonly System.Windows.Forms.Timer _tick;
    private readonly System.Windows.Forms.Timer _intro;
    private readonly float[] _bars = new float[24];

    private HudHotkeyRow? _capturing;
    private int _page;
    private float _time;
    private float _smoothed;
    private float _introProgress;
    private bool _syncing;
    private bool _layingOut;
    private bool _trayHintShown;
    private int _dragX;
    private int _dragY;
    private bool _dragging;

    private sealed class Page {
        public required HudNavItem Nav { get; init; }
        public required Panel Body { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public List<Control> Blocks { get; } = [];
    }

    public AppWindow(IAppWindowHost host) {
        _host = host;
        _version = typeof(AppWindow).Assembly.GetName().Version?.ToString(3) ?? "";

        Text = "EchoType";
        // Keep the native Windows frame. It provides reliable close/minimize hit
        // targets, DPI handling, snapping, keyboard access and accessibility.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        MinimumSize = new Size(760, 580); // eight sidebar pages
        Size = new Size(920, 680);
        BackColor = HudTheme.Paper;
        ForeColor = HudTheme.Ink;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = HudTheme.BodyText;

        _sidebar = new Panel {
            Dock = Loc.Rtl ? DockStyle.Right : DockStyle.Left,
            Width = SidebarWidthDip,
            BackColor = HudTheme.PaperSide,
            TabIndex = 0,
        };
        _sidebar.Paint += OnSidebarPaint;
        HookDrag(_sidebar);

        _header = new Panel {
            Dock = DockStyle.Top,
            Height = HeaderHeightDip,
            BackColor = HudTheme.Paper,
            TabIndex = 2,
        };
        _header.Paint += OnHeaderPaint;
        HookDrag(_header);

        _scrollHost = new Panel {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = HudTheme.Paper,
            TabIndex = 1,
        };
        _scrollHost.ClientSizeChanged += (_, _) => Relayout();

        // ── Home ──────────────────────────────────────────────
        var home = AddPage(GlyphHome, Loc.T("Home"), Loc.T("Status, model and how you dictate."));
        _statusCard = AddCard(home);
        _statusCard.Dividers = false;
        _statusCard.Inked = true;
        _statusCard.Paint += OnStatusCardPaint;
        _loginButton = MakeButton(Loc.T("Open window"), HudButton.Kind.Inverse);
        _loginButton.BackColor = HudTheme.Ink;
        _loginButton.Click += (_, _) => _host.ToggleLoginWindow();
        _statusCard.Controls.Add(_loginButton);

        AddSection(home, Loc.T("Health"));
        var health = AddCard(home);
        foreach (var (id, title) in HealthIds) {
            string captured = id;
            var fix = MakeButton(Loc.T("Open"), HudButton.Kind.Ghost);
            fix.Click += (_, _) => {
                _host.FixHealth(captured);
                SyncHealth();
            };
            _healthRows[id] = AddRow(health, Loc.T(title), Loc.T("Checking…"), fix, 150);
            _healthButtons[id] = fix;
        }

        AddSection(home, Loc.T("Model windows"));
        var models = AddCard(home);
        AddModelLauncher(models, TranscriptionProvider.ChatGpt, "ChatGPT");
        _chatgptWindowKey = AddHotkey(models, Loc.F("{0} window shortcut", "ChatGPT"), AppShortcut.ChatGptWindow);
        AddModelLauncher(models, TranscriptionProvider.Gemini, "Gemini");
        _geminiWindowKey = AddHotkey(models, Loc.F("{0} window shortcut", "Gemini"), AppShortcut.GeminiWindow);
        AddModelLauncher(models, TranscriptionProvider.Claude, "Claude");
        _claudeWindowKey = AddHotkey(models, Loc.F("{0} window shortcut", "Claude"), AppShortcut.ClaudeWindow);

        AddSection(home, Loc.T("Dictation"));
        var dictation = AddCard(home);
        _modelSeg = TranscriptionModelSegmented(provider => _host.SelectModel(provider));
        AddRow(dictation, Loc.T("Transcription model"), Loc.T("Turns your speech into text."), _modelSeg, 300);
        _modeSeg = new HudSegmented { Items = [Loc.T("Hold to talk"), Loc.T("Press to start/stop")] };
        _modeSeg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                _host.SetToggleRecording(_modeSeg.SelectedIndex == 1);
            }
        };
        AddRow(dictation, Loc.T("Recording mode"), null, _modeSeg, 300);
        _dictationKey = AddHotkey(dictation, Loc.T("Dictation key"), AppShortcut.Dictation, optional: false);
        _dictationKey.MultiKey = true;
        _secondDictationKey = AddHotkey(dictation, Loc.T("Second dictation key"), AppShortcut.SecondDictation);
        _secondDictationKey.MultiKey = true;
        _muteToggle = AddToggle(dictation, Loc.T("Pause or mute other apps while dictating"), null,
            toggle => _host.SetMuteOthers(toggle.Checked));
        _longDictationToggle = AddToggle(dictation, Loc.T("Long dictation"),
            Loc.T("If the model cuts the recording off while you're still talking, EchoType restarts it and pastes everything as one text."),
            toggle => _host.SetLongDictation(toggle.Checked));

        // ── Clean & Ask ───────────────────────────────────────
        var ai = AddPage(GlyphChat, Loc.T("Clean & Ask"), Loc.T("Models that tidy your dictation and answer your questions."));
        AddSection(ai, Loc.T("Auto Clean"));
        var clean = AddCard(ai);
        _cleanToggle = AddToggle(clean, Loc.T("Auto Clean dictation"),
            Loc.T("A second model tidies each transcript before it is pasted."),
            toggle => _host.SetAutoClean(toggle.Checked));
        _cleanSeg = ModelSegmented(provider => _host.SetCleanModel(provider));
        AddRow(clean, Loc.T("Cleaning model"), Loc.T("Also runs your custom commands."), _cleanSeg, 300);
        _cleanKey = AddHotkey(clean, Loc.T("Auto Clean on/off shortcut"), AppShortcut.AutoClean);
        var cleanPrompt = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        cleanPrompt.Click += (_, _) => _host.EditCleanPrompt();
        AddRow(clean, Loc.T("Cleaning prompt"), Loc.T("The instructions sent with each transcript."), cleanPrompt, 200);
        _cleanWindowButton = MakeButton(Loc.T("Open window"), HudButton.Kind.Ghost);
        _cleanWindowButton.Click += (_, _) => _host.ToggleCleanWindow();
        AddRow(clean, Loc.T("Model window"), Loc.T("Sign in, or watch the cleaning model work."), _cleanWindowButton, 200);

        AddSection(ai, Loc.T("Ask model"));
        var ask = AddCard(ai);
        _askSeg = ModelSegmented(provider => _host.SetAskModel(provider));
        AddRow(ask, Loc.T("Ask model"), Loc.T("Answers what you say with the Ask shortcut."), _askSeg, 300);
        _askModelKey = AddHotkey(ask, Loc.T("Ask model shortcut"), AppShortcut.AskModel);
        _askPopupToggle = AddToggle(ask, Loc.T("Show Ask model replies in a popup"),
            Loc.T("When off, the reply is pasted into the field you started in."),
            toggle => _host.SetShowAskReplyWindow(toggle.Checked));
        _askWindowButton = MakeButton(Loc.T("Open window"), HudButton.Kind.Ghost);
        _askWindowButton.Click += (_, _) => _host.ToggleAskWindow();
        AddRow(ask, Loc.T("Model window"), Loc.T("Sign in, or continue the conversation."), _askWindowButton, 200);

        // ── Translate ─────────────────────────────────────────
        var translate = AddPage(GlyphGlobe, Loc.T("Translate"), Loc.T("Speak in one language, paste in another — or translate selected text."));
        var translateCard = AddCard(translate);
        _translateKey = AddHotkey(translateCard, Loc.T("Translate shortcut"), AppShortcut.Translate);
        _translateKey.MultiKey = true;
        _translateLanguage = MakeLanguageCombo(code => _host.SetTranslateLanguage(code));
        AddRow(translateCard, Loc.T("Translate speech to"), null, _translateLanguage, 220, heightDip: 0);
        _translateSelectionKey = AddHotkey(translateCard, Loc.T("Translate selection shortcut"), AppShortcut.TranslateSelection);
        _translateSelectionKey.MultiKey = true;
        _translateSelectionLanguage = MakeLanguageCombo(code => _host.SetTranslateSelectionLanguage(code));
        AddRow(translateCard, Loc.T("Translate selection to"), null, _translateSelectionLanguage, 220, heightDip: 0);
        _chatGptTranslateToggle = AddToggle(translateCard, Loc.T("Translate with ChatGPT"),
            Loc.T("Uses chatgpt.com with your login. Off: OpenAI (with a key) or Google Translate."),
            toggle => _host.SetTranslateWithChatGpt(toggle.Checked));
        _translateWindowButton = MakeButton(Loc.T("Open window"), HudButton.Kind.Ghost);
        _translateWindowButton.Click += (_, _) => _host.ToggleTranslateWindow();
        AddRow(translateCard, Loc.T("ChatGPT translation window"), Loc.T("Sign in, or see the translation conversation."), _translateWindowButton, 200);
        _openAiButton = MakeButton(Loc.T("Add key…"), HudButton.Kind.Ghost);
        _openAiButton.Click += (_, _) => _host.EditOpenAiKey();
        _openAiRow = AddRow(translateCard, Loc.T("OpenAI API key"), null, _openAiButton, 200);

        // ── Shortcuts ─────────────────────────────────────────
        var shortcuts = AddPage(GlyphKeyboard, Loc.T("Shortcuts"), Loc.T("Keys that switch models and open windows."));
        AddSection(shortcuts, Loc.T("One key for everything"));
        var paletteCard = AddCard(shortcuts);
        _paletteKey = AddHotkey(paletteCard, Loc.T("Action palette"), AppShortcut.ActionPalette);
        _paletteKey.MultiKey = true;
        var paletteHelp = new HudSettingRow {
            Title = Loc.T("Every action from one key"),
            Description = Loc.T("Dictate, Ask, Translate, your commands, History, switch model… pick with 1–9 or type."),
        };
        AddRowControl(paletteCard, paletteHelp, 62);
        _voicePrefixToggle = AddToggle(paletteCard, Loc.T("Spoken shortcuts"),
            Loc.T("Start with \"ask …\" / \"اسأل …\" or \"translate …\" / \"ترجم …\" to route a dictation."),
            toggle => _host.SetVoicePrefixes(toggle.Checked));
        AddSection(shortcuts, Loc.T("Switch model"));
        var switchCard = AddCard(shortcuts);
        _chatgptKey = AddHotkey(switchCard, Loc.T("Switch to ChatGPT"), AppShortcut.ChatGpt);
        _geminiKey = AddHotkey(switchCard, Loc.T("Switch to Gemini"), AppShortcut.Gemini);
        _claudeKey = AddHotkey(switchCard, Loc.T("Switch to Claude (live transcript)"), AppShortcut.Claude);
        AddSection(shortcuts, Loc.T("Other shortcuts"));
        var otherKeys = AddCard(shortcuts);
        _windowKey = AddHotkey(otherKeys, Loc.T("Open model window"), AppShortcut.ModelWindow);
        AddSection(shortcuts, Loc.T("All shortcuts"));
        var allCard = AddCard(shortcuts);
        _allShortcuts = new HudCommandList { BackColor = HudTheme.Surface, MaxRows = 40, EmptyText = "No shortcuts set." };
        allCard.Controls.Add(_allShortcuts);

        // ── History ───────────────────────────────────────────
        var historyPage = AddPage(GlyphHistory, Loc.T("History"), Loc.T("Everything EchoType pasted — find it, copy it or paste it again."));
        var historyCard = AddCard(historyPage);
        var openHistory = MakeButton(Loc.T("Open history"), HudButton.Kind.Primary);
        openHistory.Click += (_, _) => _host.OpenHistory();
        AddRow(historyCard, Loc.T("History"), Loc.T("Search, copy, paste again or fix a misheard word."), openHistory, 200);
        _pasteLastKey = AddHotkey(historyCard, Loc.T("Paste last result shortcut"), AppShortcut.PasteLast);
        _pasteLastKey.MultiKey = true;
        _historyToggle = AddToggle(historyCard, Loc.T("Keep history"),
            Loc.T("Stored only on this PC (%APPDATA%\\EchoType\\history.json)."),
            toggle => _host.SetHistoryEnabled(toggle.Checked));
        _historyLimit = MakeNumber(10, 5000, 10, value => _host.SetHistoryLimit(value));
        AddRow(historyCard, Loc.T("Entries to keep"), Loc.T("The oldest are removed first."), _historyLimit, 120, heightDip: 0);

        // ── Commands ──────────────────────────────────────────
        var commands = AddPage(GlyphCommand, Loc.T("Commands"), Loc.T("Custom commands, workspace modes and word replacements."));
        AddSection(commands, Loc.T("Custom commands"));
        var commandsCard = AddCard(commands);
        var editCommands = MakeButton(Loc.T("Add or edit…"), HudButton.Kind.Ghost);
        editCommands.Click += (_, _) => _host.OpenCommands();
        AddRow(commandsCard, Loc.T("Custom commands"), Loc.T("Your own prompts, run on what you dictate."), editCommands, 200);
        _commands = new HudCommandList { BackColor = HudTheme.Surface };
        commandsCard.Controls.Add(_commands);

        AddSection(commands, Loc.T("Voice & text"));
        var voiceCard = AddCard(commands);
        var modesButton = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        modesButton.Click += (_, _) => _host.EditWorkspaceModes();
        AddRow(voiceCard, Loc.T("Workspace modes"), Loc.T("Say a word to press a shortcut."), modesButton, 200);
        var wordsButton = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        wordsButton.Click += (_, _) => _host.EditWordReplacements();
        AddRow(voiceCard, Loc.T("Find & replace"), Loc.T("Correct words or phrases in every transcript."), wordsButton, 200);
        _addWordKey = AddHotkey(voiceCard, Loc.T("Add word shortcut"), AppShortcut.AddWordReplacement);
        _addWordKey.MultiKey = true;

        AddSection(commands, Loc.T("Text shortcuts"));
        var textCard = AddCard(commands);
        _textShortcutsToggle = AddToggle(textCard, Loc.T("Expand text shortcuts"),
            Loc.T("Type a shortcut such as وع, press Space, and it becomes its text — in any app."),
            toggle => _host.SetTextShortcutsEnabled(toggle.Checked));
        var textButton = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        textButton.Click += (_, _) => _host.EditTextShortcuts();
        _textShortcutsRow = AddRow(textCard, Loc.T("Text shortcuts"),
            Loc.F("{0} shortcuts", _host.Settings.TextShortcuts.Count), textButton, 200);

        // ── Settings ──────────────────────────────────────────
        var settings = AddPage(GlyphSettings, Loc.T("Settings"), Loc.T("General options, language and app controls."));
        AddSection(settings, Loc.T("Paste"));
        var paste = AddCard(settings);
        _enterToggle = AddToggle(paste, Loc.T("Press Enter after paste"), null,
            toggle => _host.SetPressEnterAfterPaste(toggle.Checked));
        _enterKey = AddHotkey(paste, Loc.T("Auto Enter shortcut"), AppShortcut.PressEnter);
        _clipboardToggle = AddToggle(paste, Loc.T("Keep transcript on clipboard"), null,
            toggle => _host.SetKeepTranscriptOnClipboard(toggle.Checked));
        var profilesButton = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        profilesButton.Click += (_, _) => _host.EditAppProfiles();
        AddRow(paste, Loc.T("Per-app profiles"), Loc.T("Auto Enter, Auto Clean and Translate per app."), profilesButton, 200);

        AddSection(settings, Loc.T("Language") + (Loc.Arabic ? "" : " · اللغة"));
        var languageCard = AddCard(settings);
        // Each name in its own language so it can be found whatever the current UI is.
        _languageSeg = new HudSegmented { Items = ["English", "العربية"] };
        _languageSeg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                _host.SetUiLanguage(_languageSeg.SelectedIndex == 1 ? Loc.ArabicCode : Loc.English);
            }
        };
        AddRow(languageCard, Loc.T("Interface language"), Loc.T("EchoType restarts to apply it."), _languageSeg, 240);

        AddSection(settings, Loc.T("App"));
        var about = AddCard(settings);
        _startupToggle = AddToggle(about, Loc.T("Start EchoType with Windows"), null,
            toggle => _host.SetStartWithWindows(toggle.Checked));
        var openLog = MakeButton(Loc.T("Open log"), HudButton.Kind.Ghost);
        openLog.Click += (_, _) => _host.OpenLog();
        AddRow(about, Loc.T("Diagnostics log"), Loc.T("Open the log when something goes wrong."), openLog, 160);
        var setup = MakeButton(Loc.T("Run setup"), HudButton.Kind.Ghost);
        setup.Click += (_, _) => _host.ShowSetupWizard();
        AddRow(about, Loc.T("Setup"), Loc.T("Microphone, sign-in, recording mode and a test dictation."), setup, 160);
        var quit = MakeButton(Loc.T("Quit"), HudButton.Kind.Danger);
        quit.Click += (_, _) => _host.Quit();
        AddRow(about, Loc.T("Quit EchoType"), Loc.T("Stops dictation and removes the tray icon."), quit, 160);

        AddSection(settings, Loc.T("Updates & reliability"));
        var updates = AddCard(settings);
        _updateButton = MakeButton(Loc.T("Check now"), HudButton.Kind.Ghost);
        _updateButton.Click += (_, _) => {
            if (_host.UpdateAvailable) {
                _host.InstallUpdate();
            } else {
                _host.CheckForUpdates();
            }
        };
        _updateRow = AddRow(updates, Loc.T("EchoType updates"), "", _updateButton, 200);
        var skip = MakeButton(Loc.T("Skip this version"), HudButton.Kind.Ghost);
        skip.Click += (_, _) => _host.SkipUpdate();
        _skipUpdateRow = AddRow(updates, Loc.T("Not now"), Loc.T("No reminder until a newer version comes out."), skip, 200);
        _updatesToggle = AddToggle(updates, Loc.T("Check for updates automatically"), Loc.T("Once a day, from GitHub Releases."),
            toggle => _host.SetCheckForUpdates(toggle.Checked));
        _remoteSelectorsToggle = AddToggle(updates, Loc.T("Download page fixes"),
            Loc.T("When ChatGPT, Gemini or Claude change their page, a fix reaches you without a new version."),
            toggle => _host.SetRemoteSelectors(toggle.Checked));
        _failoverToggle = AddToggle(updates, Loc.T("Switch models when one stops working"),
            Loc.T("Signed out or page changed? EchoType moves to another signed-in model."),
            toggle => _host.SetAutoFailover(toggle.Checked));

        AddSection(settings, Loc.T("Backup"));
        var backup = AddCard(settings);
        var export = MakeButton(Loc.T("Export…"), HudButton.Kind.Ghost);
        export.Click += (_, _) => _host.ExportSettings();
        AddRow(backup, Loc.T("Export settings"), Loc.T("Commands, word fixes, shortcuts and options in one file (no API key)."), export, 160);
        var import = MakeButton(Loc.T("Import…"), HudButton.Kind.Ghost);
        import.Click += (_, _) => _host.ImportSettings();
        AddRow(backup, Loc.T("Import settings"), Loc.T("Merge a file exported on another PC or shared by a friend."), import, 160);

        // ── Advanced ──────────────────────────────────────────
        var advanced = AddPage(GlyphAdvanced, Loc.T("Advanced"), Loc.T("Options that used to live only in config.json."));
        var advancedCard = AddCard(advanced);
        _pillMs = MakeNumber(0, 600_000, 100, value => _host.SetProgressPillHideMs(value));
        AddRow(advancedCard, Loc.T("Progress pill time (ms)"),
            Loc.T("How long the Translate selection pill stays up. 0 keeps it until the work ends."), _pillMs, 120, heightDip: 0);
        _openAiModel = new TextBox {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = HudTheme.BodyText,
            PlaceholderText = "gpt-4o-mini",
        };
        _openAiModel.Leave += (_, _) => _host.SetOpenAiTranslationModel(_openAiModel.Text);
        _openAiModel.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Enter) {
                _host.SetOpenAiTranslationModel(_openAiModel.Text);
                e.SuppressKeyPress = true;
            }
        };
        AddRow(advancedCard, Loc.T("OpenAI translation model"), Loc.T("Used only with an OpenAI API key."), _openAiModel, 200, heightDip: 0);
        var sounds = MakeButton(Loc.T("Open folder"), HudButton.Kind.Ghost);
        sounds.Click += (_, _) => _host.OpenSoundsFolder();
        AddRow(advancedCard, Loc.T("Custom sounds"), Loc.T("Put start.wav and done.wav here, then restart EchoType."), sounds, 160);
        var config = MakeButton(Loc.T("Open folder"), HudButton.Kind.Ghost);
        config.Click += (_, _) => _host.OpenConfigFolder();
        AddRow(advancedCard, Loc.T("Configuration folder"), Loc.T("config.json, selectors.json and history.json."), config, 160);

        // Search box in the header: finds any setting by its words and jumps to it.
        _search = new TextBox {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = HudTheme.BodyText,
            PlaceholderText = Loc.T("Search settings…"),
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        _search.TextChanged += (_, _) => UpdateSearch();
        _search.KeyDown += OnSearchKeyDown;
        _search.Leave += (_, _) => BeginInvoke(() => {
            if (_searchResults is { Focused: false } results) {
                results.Visible = false;
            }
        });
        _header.Controls.Add(_search);
        _header.Resize += (_, _) => PlaceSearch();
        _searchResults = new ListBox {
            Visible = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = HudTheme.BodyText,
            IntegralHeight = false,
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        _searchResults.MouseClick += (_, e) => {
            int i = _searchResults.IndexFromPoint(e.Location);
            if (i >= 0) {
                JumpTo(i);
            }
        };
        _searchResults.KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Enter) {
                JumpTo(_searchResults.SelectedIndex);
                e.Handled = true;
            } else if (e.KeyCode == Keys.Escape) {
                _searchResults.Visible = false;
                _search.Select();
                e.Handled = true;
            }
        };
        _searchResults.Leave += (_, _) => _searchResults.Visible = false;
        Controls.Add(_searchResults);

        // Dock order: the sidebar takes the full height, the header tops the rest.
        Controls.Add(_scrollHost);
        Controls.Add(_header);
        Controls.Add(_sidebar);

        _tick = new System.Windows.Forms.Timer { Interval = 16 };
        _tick.Tick += (_, _) => OnTick();
        _intro = new System.Windows.Forms.Timer { Interval = 16 };
        _intro.Tick += (_, _) => StepIntro();
        // Model pages sign in, load and break on their own; refresh the Health card while it shows.
        _healthTick = new System.Windows.Forms.Timer { Interval = 2000 };
        _healthTick.Tick += (_, _) => {
            if (Visible && _page == 0) {
                SyncHealth();
            }
        };

        host.UiChanged += OnHostChanged;
        Load += (_, _) => {
            HudTheme.ApplyChrome(this, paper: true);
            ApplyChromeMetrics();
            ShowPage(_lastPage);
            SyncFromHost();
            Opacity = 0;
            _intro.Start();
            _tick.Start();
            _healthTick.Start();
            PlaceSearch();
        };
        Resize += (_, _) => {
            Relayout();
            _header.Invalidate();
        };
        Deactivate += (_, _) => StopCapture();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e);
        ApplyChromeMetrics();
        Relayout();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        HudTheme.ApplyChrome(this, paper: true);
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        HudTheme.ApplyChrome(this, paper: true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e) {
        if (e.CloseReason == CloseReason.UserClosing) {
            e.Cancel = true;
            StopCapture();
            Hide();
            if (!_trayHintShown) {
                _trayHintShown = true;
                StatusOverlay.ShowNotice(
                    Loc.T("Still running in the tray. Double-click the icon to open EchoType again."));
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.KeyCode == Keys.Escape && _capturing != null) {
            StopCapture();
            e.Handled = true;
        } else if (e.Control && e.KeyCode == Keys.F && _capturing == null) {
            _search.Select();
            _search.SelectAll();
            e.Handled = true;
        } else if (e.Control && e.KeyCode == Keys.Tab && _capturing == null) {
            // Ctrl+Tab / Ctrl+Shift+Tab step through the pages.
            ShowPage((_page + (e.Shift ? _pages.Count - 1 : 1)) % _pages.Count);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _host.UiChanged -= OnHostChanged;
            _host.Hotkey.KeyCaptured -= OnKeyCaptured;
            _host.Hotkey.ChordCaptured -= OnChordCaptured;
            _host.Hotkey.ChordReleased -= OnChordReleased;
            _tick.Dispose();
            _intro.Dispose();
            _healthTick.Dispose();
        }
        base.Dispose(disposing);
    }

    public void Reveal() {
        if (IsDisposed) {
            return;
        }
        if (WindowState == FormWindowState.Minimized) {
            WindowState = FormWindowState.Normal;
        }
        Show();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        if (Opacity < 1 && !_intro.Enabled) {
            Opacity = 1;
        }
    }

    private void OnHostChanged() {
        if (IsDisposed) {
            return;
        }
        if (InvokeRequired) {
            BeginInvoke(OnHostChanged);
            return;
        }
        SyncFromHost();
    }

    private void SyncFromHost() {
        _syncing = true;
        try {
            var s = _host.Settings;
            _modelSeg.SetSilent(s.TranscriptionProvider == TranscriptionProvider.Gemini ? 0 : 1);
            _cleanToggle.SetSilent(s.AutoClean);
            _cleanSeg.SetSilent(ProviderIndex(s.CleanProvider));
            _cleanWindowButton.Text = Loc.F(_host.CleanWindowVisible ? "Close {0} window" : "Open {0} window", _host.CleanModelName);
            _askSeg.SetSilent(ProviderIndex(s.AskProvider));
            _askWindowButton.Text = Loc.F(_host.AskWindowVisible ? "Close {0} window" : "Open {0} window", _host.AskModelName);
            _modeSeg.SetSilent(s.ToggleRecording ? 1 : 0);
            _muteToggle.SetSilent(s.MuteOtherAppsWhileDictating);
            _longDictationToggle.SetSilent(s.LongDictation);
            _textShortcutsToggle.SetSilent(s.TextShortcutsEnabled);
            _textShortcutsRow.Description = Loc.F("{0} shortcuts", s.TextShortcuts.Count);
            _textShortcutsRow.Invalidate();
            _enterToggle.SetSilent(s.PressEnterAfterPaste);
            _clipboardToggle.SetSilent(s.KeepTranscriptOnClipboard);
            _startupToggle.SetSilent(s.StartWithWindows);
            _askPopupToggle.SetSilent(s.ShowAskReplyWindow);
            _historyToggle.SetSilent(s.HistoryEnabled);
            SetNumberSilent(_historyLimit, s.HistoryLimit);
            _voicePrefixToggle.SetSilent(s.VoicePrefixes);
            _chatGptTranslateToggle.SetSilent(s.TranslateWithChatGpt);
            _translateWindowButton.Text = Loc.T(_host.TranslateWindowVisible ? "Close window" : "Open window");
            _updatesToggle.SetSilent(s.CheckForUpdates);
            _remoteSelectorsToggle.SetSilent(s.RemoteSelectors);
            _failoverToggle.SetSilent(s.AutoFailover);
            _updateRow.Description = _host.UpdateStatus;
            _updateRow.Invalidate();
            _updateButton.Text = _host.UpdateAvailable ? Loc.T("Install update") : Loc.T("Check now");
            _updateButton.Enabled = !_host.UpdateBusy;
            SetCollapsed(_skipUpdateRow, !_host.UpdateAvailable, relayout: false);
            SetNumberSilent(_pillMs, s.ProgressPillHideMs);
            if (!_openAiModel.Focused) {
                _openAiModel.Text = s.OpenAiTranslationModel;
            }
            if (_capturing != _pasteLastKey) {
                _pasteLastKey.Value = HotkeyNames.For(s.PasteLastChord);
            }
            if (_capturing != _paletteKey) {
                _paletteKey.Value = HotkeyNames.For(s.ActionPaletteChord);
            }
            _allShortcuts.Reload(ShortcutOverview(s));
            SyncHealth(relayout: false);
            _languageSeg.SetSilent(Loc.Arabic ? 1 : 0);
            if (_capturing != _dictationKey) {
                _dictationKey.Value = HotkeyNames.For(s.DictationChord);
            }
            if (_capturing != _secondDictationKey) {
                _secondDictationKey.Value = HotkeyNames.For(s.SecondDictationChord);
            }
            _askModelKey.Value = HotkeyLabel(s.AskModelVk);
            if (_capturing != _translateKey) {
                _translateKey.Value = HotkeyNames.For(s.TranslateChord);
            }
            if (_capturing != _translateSelectionKey) {
                _translateSelectionKey.Value = HotkeyNames.For(s.TranslateSelectionChord);
            }
            if (_capturing != _addWordKey) {
                _addWordKey.Value = HotkeyNames.For(s.AddWordReplacementChord);
            }
            _translateLanguage.SelectedIndex = IndexOfLanguage(_translateLanguage, s.TranslateTargetLanguage);
            _translateSelectionLanguage.SelectedIndex = IndexOfLanguage(_translateSelectionLanguage, s.TranslateSelectionTargetLanguage);
            bool hasKey = !string.IsNullOrWhiteSpace(s.OpenAiApiKey);
            _openAiRow.Description = Loc.T(hasKey ? "Set — translating with OpenAI." : "Not set — translating with Google.");
            _openAiButton.Text = Loc.T(hasKey ? "Change…" : "Add key…");
            _openAiRow.Invalidate();
            _chatgptKey.Value = HotkeyLabel(s.ChatGptSwitchVk);
            _geminiKey.Value = HotkeyLabel(s.GeminiSwitchVk);
            _claudeKey.Value = HotkeyLabel(s.ClaudeSwitchVk);
            _enterKey.Value = HotkeyLabel(s.PressEnterToggleVk);
            _cleanKey.Value = HotkeyLabel(s.AutoCleanToggleVk);
            _windowKey.Value = HotkeyLabel(s.OpenModelWindowVk);
            _chatgptWindowKey.Value = HotkeyLabel(s.OpenChatGptWindowVk);
            _geminiWindowKey.Value = HotkeyLabel(s.OpenGeminiWindowVk);
            _claudeWindowKey.Value = HotkeyLabel(s.OpenClaudeWindowVk);
            foreach (var row in new[] { _dictationKey, _secondDictationKey, _askModelKey, _translateKey, _translateSelectionKey, _addWordKey, _pasteLastKey, _paletteKey, _chatgptKey, _geminiKey, _claudeKey, _enterKey, _cleanKey, _windowKey, _chatgptWindowKey, _geminiWindowKey, _claudeWindowKey }) {
                row.Invalidate();
            }
            _loginButton.Text = !_host.Online ? Loc.T("Retry connection")
                : _host.LoginWindowVisible ? Loc.F("Close {0} window", _host.ModelName)
                : _host.LoggedIn ? Loc.F("Open {0} window", _host.ModelName)
                : Loc.F("Log in to {0}", _host.ModelName);
            _commands.Reload(s.ActiveCommands.Select(c => {
                c.Normalize();
                return (c.DisplayName, HotkeyNames.For(c.Chord));
            }).ToList());
            Icon = TrayIcons.For(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
            _statusCard.Invalidate();
            _sidebar.Invalidate();
            Relayout();
        } finally {
            _syncing = false;
        }
    }

    private void OnTick() {
        if (IsDisposed || !Visible) {
            return;
        }
        _time += 0.016f;
        if (_host.Phase == AppPhase.Listening) {
            if (_host.CopyMicSpectrum(_spectrum)) {
                HudTheme.StepSpectrum(_bars, ref _smoothed, _spectrum, _host.MicLevel);
            } else {
                HudTheme.StepWave(_bars, ref _smoothed, _host.MicLevel, _time);
            }
            _statusCard.Invalidate();
            _sidebar.Invalidate(new Rectangle(0, 0, _sidebar.Width, HudTheme.Dip(this, NavTopDip)));
        } else if (_smoothed > 0.01f) {
            _smoothed *= 0.86f;
            Array.Clear(_bars);
            _statusCard.Invalidate();
            _sidebar.Invalidate(new Rectangle(0, 0, _sidebar.Width, HudTheme.Dip(this, NavTopDip)));
        }
    }

    private void StepIntro() {
        _introProgress = Math.Clamp(_introProgress + 16f / HudTheme.InDurationMs, 0, 1);
        float eased = HudTheme.EaseOutCubic(_introProgress);
        Opacity = eased;
        if (_introProgress >= 1) {
            Opacity = 1;
            _intro.Stop();
        }
    }

    // ── Painting ──────────────────────────────────────────────

    private void OnSidebarPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.PaperSide);
        int w = _sidebar.Width;

        // Brand: a black rounded tile with a white voice wave that moves while you speak.
        int markSize = HudTheme.Dip(this, 34);
        var mark = Loc.Mirror(new Rectangle(HudTheme.Dip(this, 22), HudTheme.Dip(this, 26), markSize, markSize), w);
        using (var path = HudTheme.Rounded(mark, HudTheme.Dip(this, 10)))
        using (var tile = new SolidBrush(HudTheme.Ink)) {
            e.Graphics.FillPath(tile, path);
        }
        float[] rest = [0.38f, 0.7f, 1f, 0.62f, 0.34f];
        float barW = HudTheme.DipF(this, 2.6f);
        float barGap = HudTheme.DipF(this, 2.4f);
        float barsW = rest.Length * barW + (rest.Length - 1) * barGap;
        float barX = mark.Left + (mark.Width - barsW) / 2f;
        float midY = mark.Top + mark.Height / 2f;
        float maxBar = mark.Height * 0.56f;
        bool listening = _host.Phase == AppPhase.Listening;
        using (var white = new SolidBrush(HudTheme.OnInk)) {
            for (int i = 0; i < rest.Length; i++) {
                float live = listening ? _bars[Math.Min(_bars.Length - 1, 6 + i * 3)] : 0f;
                float h = Math.Max(barW, maxBar * Math.Clamp(rest[i] * (0.55f + 0.45f * _smoothed) + live * 0.5f, 0.2f, 1f));
                using var bar = HudTheme.Rounded(new RectangleF(barX + i * (barW + barGap), midY - h / 2f, barW, h), barW / 2f);
                e.Graphics.FillPath(white, bar);
            }
        }

        int textLeft = HudTheme.Dip(this, 22 + 34 + 12);
        int textWidth = Math.Max(0, w - textLeft - HudTheme.Dip(this, 16));
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, "EchoType", HudTheme.Caption,
            Loc.Mirror(new Rectangle(textLeft, HudTheme.Dip(this, 24), textWidth, HudTheme.Dip(this, 22)), w),
            HudTheme.Ink, Loc.Flags(flags));

        // Phase: a small status dot (pulsing while listening) and its label.
        Color accent = HudTheme.PaperAccent(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        float pulse = listening ? 0.6f + 0.4f * MathF.Sin(_time * 5.5f) : 1f;
        float dotR = HudTheme.DipF(this, 3.5f);
        float dotX = textLeft + dotR;
        float dotY = HudTheme.DipF(this, 49 + 7);
        if (Loc.Rtl) {
            dotX = w - dotX;
        }
        using (var dot = new SolidBrush(Color.FromArgb((int)(255 * pulse), accent))) {
            e.Graphics.FillEllipse(dot, dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);
        }
        int phaseLeft = textLeft + HudTheme.Dip(this, 12);
        TextRenderer.DrawText(e.Graphics, HudTheme.PhaseLabel(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady),
            HudTheme.Tiny,
            Loc.Mirror(new Rectangle(phaseLeft, HudTheme.Dip(this, 48), Math.Max(0, textWidth - HudTheme.Dip(this, 12)), HudTheme.Dip(this, 16)), w),
            HudTheme.InkSoft, Loc.Flags(flags));

        TextRenderer.DrawText(e.Graphics, Loc.F("Version {0}", _version), HudTheme.Tiny,
            Loc.Mirror(new Rectangle(HudTheme.Dip(this, 26), _sidebar.Height - HudTheme.Dip(this, 36), textWidth, HudTheme.Dip(this, 18)), w),
            HudTheme.InkMuted, Loc.Flags(flags));

        // Hairline between the sidebar and the page.
        using var line = new SolidBrush(HudTheme.Hairline);
        e.Graphics.FillRectangle(line, Loc.Rtl ? 0 : w - 1, 0, 1, _sidebar.Height);
    }

    private void OnHeaderPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Paper);
        if (_pages.Count == 0) {
            return;
        }
        var page = _pages[_page];
        int w = _header.Width;
        int left = HudTheme.Dip(this, PagePadDip + 4);
        int right = Math.Max(left + 40, w - left - SearchWidth() - HudTheme.Dip(this, 16));
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, page.Title, HudTheme.Hero,
            Loc.Mirror(new Rectangle(left, HudTheme.Dip(this, 28), right - left, HudTheme.Dip(this, 38)), w),
            HudTheme.Ink, Loc.Flags(flags));
        // The subtitle may run under the caption buttons; they sit higher.
        TextRenderer.DrawText(e.Graphics, page.Subtitle, HudTheme.Small,
            Loc.Mirror(new Rectangle(left, HudTheme.Dip(this, 68), w - left * 2, HudTheme.Dip(this, 20)), w),
            HudTheme.InkMuted, Loc.Flags(flags));
    }

    private void OnStatusCardPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        int cw = _statusCard.Width;
        int left = HudTheme.Dip(this, 28);
        int trailing = cw - _loginButton.Width - HudTheme.Dip(this, 24 + 20);
        bool listening = _host.Phase == AppPhase.Listening;
        int waveWidth = HudTheme.Dip(this, 132);
        int textWidth = Math.Max(0, trailing - left - (listening ? waveWidth + HudTheme.Dip(this, 16) : 0));

        string phase = HudTheme.PhaseLabel(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        string body = !_host.Online ? Loc.T("No internet")
            : !_host.LoggedIn ? _host.ModelName + " · " + Loc.T("Sign in to dictate")
            : _host.StatusText;
        int chipH = HudTheme.Dip(this, 18);
        int titleH = HudTheme.Dip(this, 36);
        int bodyH = HudTheme.Dip(this, 20);
        int gap = HudTheme.Dip(this, 6);
        int top = (_statusCard.Height - chipH - gap - titleH - bodyH) / 2;
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        // Status line: a coloured dot and the model, like a small eyebrow above the phase.
        Color accent = HudTheme.PhaseAccent(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        float dotR = HudTheme.DipF(this, 4);
        float dotX = left + dotR;
        if (Loc.Rtl) {
            dotX = cw - dotX;
        }
        float dotY = top + chipH / 2f;
        using (var glow = new SolidBrush(Color.FromArgb(60, accent))) {
            float g = dotR * (listening ? 2.2f + 0.6f * MathF.Sin(_time * 5.5f) : 2f);
            e.Graphics.FillEllipse(glow, dotX - g, dotY - g, g * 2, g * 2);
        }
        using (var dot = new SolidBrush(accent)) {
            e.Graphics.FillEllipse(dot, dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);
        }
        int eyebrowLeft = left + HudTheme.Dip(this, 18);
        TextRenderer.DrawText(e.Graphics, _host.ModelName, HudTheme.SmallBold,
            Loc.Mirror(new Rectangle(eyebrowLeft, top, Math.Max(0, textWidth - HudTheme.Dip(this, 18)), chipH), cw),
            HudTheme.OnInkMuted, Loc.Flags(flags | TextFormatFlags.VerticalCenter));

        TextRenderer.DrawText(e.Graphics, phase, HudTheme.Hero,
            Loc.Mirror(new Rectangle(left, top + chipH + gap, textWidth, titleH), cw),
            HudTheme.OnInk, Loc.Flags(flags));
        TextRenderer.DrawText(e.Graphics, body, HudTheme.Small,
            Loc.Mirror(new Rectangle(left, top + chipH + gap + titleH, textWidth, bodyH), cw),
            HudTheme.OnInkMuted, Loc.Flags(flags));

        if (listening) {
            int waveH = HudTheme.Dip(this, 40);
            var wave = Loc.Mirror(new Rectangle(trailing - waveWidth, (_statusCard.Height - waveH) / 2, waveWidth, waveH), cw);
            HudTheme.DrawWave(e.Graphics, wave, _bars, HudTheme.OnInk, HudTheme.DipF(this, 18));
        }
    }

    // ── Dragging the borderless window ────────────────────────

    private void HookDrag(Control surface) {
        surface.MouseDown += OnDragMouseDown;
        surface.MouseMove += OnDragMouseMove;
        surface.MouseUp += (_, _) => _dragging = false;
    }

    private void OnDragMouseDown(object? sender, MouseEventArgs e) {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) {
            return;
        }
        _dragging = true;
        _dragX = e.X;
        _dragY = e.Y;
    }

    private void OnDragMouseMove(object? sender, MouseEventArgs e) {
        if (!_dragging) {
            return;
        }
        Location = new Point(Location.X + e.X - _dragX, Location.Y + e.Y - _dragY);
    }

    // ── Layout ────────────────────────────────────────────────

    private void ApplyChromeMetrics() {
        _sidebar.Width = HudTheme.Dip(this, SidebarWidthDip);
        _header.Height = HudTheme.Dip(this, HeaderHeightDip);
        _translateLanguage.ItemHeight = HudTheme.Dip(this, 28);
        _translateSelectionLanguage.ItemHeight = HudTheme.Dip(this, 28);
    }

    private void ShowPage(int index) {
        index = Math.Clamp(index, 0, _pages.Count - 1);
        StopCapture();
        _page = index;
        _lastPage = index;
        for (int i = 0; i < _pages.Count; i++) {
            _pages[i].Nav.Selected = i == index;
        }
        _scrollHost.SuspendLayout();
        _scrollHost.AutoScrollPosition = Point.Empty;
        for (int i = 0; i < _pages.Count; i++) {
            _pages[i].Body.Visible = i == index;
        }
        _scrollHost.ResumeLayout();
        Relayout();
        _header.Invalidate();
    }

    private void Relayout() {
        if (_pages.Count == 0 || _layingOut) {
            return;
        }
        _layingOut = true;
        try {
            int navX = HudTheme.Dip(this, 14);
            int navY = HudTheme.Dip(this, NavTopDip);
            int navW = _sidebar.Width - navX * 2;
            foreach (var p in _pages) {
                p.Nav.SetBounds(navX, navY, navW, HudTheme.Dip(this, 40));
                navY += HudTheme.Dip(this, 44);
            }

            var page = _pages[_page];
            int width = _scrollHost.Width;
            if (LayoutPage(page, width) > _scrollHost.Height) {
                // Make room for the vertical scrollbar up front so a horizontal one never appears.
                width -= SystemInformation.VerticalScrollBarWidth;
            }
            int height = LayoutPage(page, width);
            page.Body.SetBounds(0, _scrollHost.AutoScrollPosition.Y, width, height);
        } finally {
            _layingOut = false;
        }
    }

    /// <summary>Stacks the page's section labels and cards; returns the content height.</summary>
    private int LayoutPage(Page page, int width) {
        int pad = HudTheme.Dip(this, PagePadDip);
        int inner = Math.Max(HudTheme.Dip(this, 280), width - pad * 2);
        int y = 0;
        bool first = true;
        foreach (var block in page.Blocks) {
            if (block is HudSectionLabel) {
                int h = HudTheme.Dip(this, first ? 28 : 46);
                block.SetBounds(pad, y, inner, h);
                y += h;
            } else if (block is HudCard card) {
                card.Width = inner;
                if (card == _statusCard) {
                    LayoutStatusCard();
                } else {
                    LayoutCard(card);
                }
                card.Location = new Point(pad, y);
                y += card.Height + HudTheme.Dip(this, card == _statusCard ? 8 : 4);
                card.Invalidate();
            }
            first = false;
        }
        return y + HudTheme.Dip(this, 24);
    }

    private void LayoutStatusCard() {
        _statusCard.Height = HudTheme.Dip(this, 136);
        int w = Math.Min(HudTheme.Dip(this, 220), _statusCard.Width / 2);
        int h = HudTheme.Dip(this, 42);
        var r = new Rectangle(_statusCard.Width - w - HudTheme.Dip(this, 24), (_statusCard.Height - h) / 2, w, h);
        _loginButton.Bounds = Loc.Mirror(r, _statusCard.Width);
    }

    /// <summary>Stacks a card's rows full width, a hairline gap between each.</summary>
    private void LayoutCard(HudCard card) {
        int padX = HudTheme.Dip(this, 20);
        int padY = HudTheme.Dip(this, 4);
        int gap = Math.Max(1, HudTheme.Dip(this, 1));
        int inner = Math.Max(40, card.Width - padX * 2);
        int y = padY;
        foreach (Control child in card.Controls) {
            bool show = !_collapsed.Contains(child);
            child.Visible = show;
            if (!show) {
                continue;
            }
            int h = _rowHeights.TryGetValue(child, out int dip) ? HudTheme.Dip(this, dip) : child.Height;
            if (child is HudCommandList) {
                // Breathing room above and below the list.
                h += HudTheme.Dip(this, 12);
            }
            child.SetBounds(padX, y, inner, h);
            y += h + gap;
        }
        card.Height = Math.Max(padY * 2, y - gap + padY);
    }

    // ── Building blocks ───────────────────────────────────────

    private Page AddPage(string glyph, string title, string subtitle) {
        var nav = new HudNavItem { Glyph = glyph, Text = title };
        int index = _pages.Count;
        nav.Click += (_, _) => ShowPage(index);
        _sidebar.Controls.Add(nav);
        var body = new Panel { BackColor = HudTheme.Paper, Visible = false };
        _scrollHost.Controls.Add(body);
        var page = new Page { Nav = nav, Body = body, Title = title, Subtitle = subtitle };
        _pages.Add(page);
        return page;
    }

    private static void AddSection(Page page, string text) {
        var label = new HudSectionLabel { Text = text };
        page.Body.Controls.Add(label);
        page.Blocks.Add(label);
    }

    private HudCard AddCard(Page page) {
        var card = new HudCard();
        page.Body.Controls.Add(card);
        page.Blocks.Add(card);
        _cardPage[card] = _pages.IndexOf(page);
        return card;
    }

    private void IndexRow(HudCard card, Control row, string title, string? description) {
        if (_cardPage.TryGetValue(card, out int page) && !string.IsNullOrWhiteSpace(title)) {
            _searchIndex.Add(new SearchEntry(title, description, page, row));
        }
    }

    private void AddRowControl(HudCard card, Control control, int heightDip) {
        control.BackColor = HudTheme.Surface;
        _rowHeights[control] = heightDip;
        card.Controls.Add(control);
    }

    private HudSettingRow AddRow(HudCard card, string title, string? description, Control trailing, int widthDip, int heightDip = 36) {
        var row = new HudSettingRow { Title = title, Description = description };
        if (trailing is HudButton or HudSegmented) {
            trailing.BackColor = HudTheme.Surface;
        }
        row.SetTrailing(trailing, widthDip, heightDip);
        AddRowControl(card, row, description == null ? 56 : 62);
        IndexRow(card, row, title, description);
        return row;
    }

    private HudHotkeyRow AddHotkey(HudCard card, string title, AppShortcut shortcut, bool optional = true) {
        var row = new HudHotkeyRow {
            Title = title,
            Optional = optional,
            Font = Font,
        };
        row.CaptureRequested += (_, _) => BeginCapture(row, shortcut);
        row.ClearRequested += (_, _) => {
            if (optional) {
                StopCapture();
                _host.TrySetShortcut(shortcut, 0);
            }
        };
        AddRowControl(card, row, 62);
        IndexRow(card, row, title, Loc.T("Shortcut"));
        return row;
    }

    private HudToggleRow AddToggle(HudCard card, string text, string? description, Action<HudToggleRow> changed) {
        var row = new HudToggleRow { Text = text, Description = description, Font = Font };
        row.CheckedChanged += (_, _) => {
            if (!_syncing) {
                changed(row);
            }
        };
        AddRowControl(card, row, description == null ? 52 : 62);
        IndexRow(card, row, text, description);
        return row;
    }

    private HudSegmented ModelSegmented(Action<TranscriptionProvider> select) {
        var seg = new HudSegmented { Items = ["ChatGPT", "Gemini", "Claude"] };
        seg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                select(seg.SelectedIndex switch {
                    1 => TranscriptionProvider.Gemini,
                    2 => TranscriptionProvider.Claude,
                    _ => TranscriptionProvider.ChatGpt,
                });
            }
        };
        return seg;
    }

    /// <summary>Gemini / Claude only — ChatGPT can't transcribe (TranscriptionProviders.CanTranscribe).</summary>
    private HudSegmented TranscriptionModelSegmented(Action<TranscriptionProvider> select) {
        var seg = new HudSegmented { Items = ["Gemini", "Claude"] };
        seg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                select(seg.SelectedIndex == 0 ? TranscriptionProvider.Gemini : TranscriptionProvider.Claude);
            }
        };
        return seg;
    }

    private void AddModelLauncher(HudCard card, TranscriptionProvider provider, string name) {
        var open = MakeButton(Loc.T("Open"), HudButton.Kind.Ghost);
        open.Click += (_, _) => _host.OpenModel(provider);
        AddRow(card, name, Loc.T("Open this model directly."), open, 150);
    }

    private static int ProviderIndex(TranscriptionProvider provider) => provider switch {
        TranscriptionProvider.Gemini => 1,
        TranscriptionProvider.Claude => 2,
        _ => 0,
    };

    private static HudButton MakeButton(string text, HudButton.Kind kind) {
        return new HudButton {
            Text = text,
            StyleKind = kind,
            Font = HudTheme.SmallBold,
            Height = 36,
        };
    }

    // ── Shortcut capture ──────────────────────────────────────

    private void BeginCapture(HudHotkeyRow row, AppShortcut shortcut) {
        if (_capturing == row) {
            StopCapture();
            return;
        }
        StopCapture();
        _capturing = row;
        row.Capturing = true;
        row.Tag = shortcut;
        _host.Hotkey.ResetCapture();
        if (shortcut is AppShortcut.Translate or AppShortcut.TranslateSelection or AppShortcut.Dictation
            or AppShortcut.SecondDictation or AppShortcut.AddWordReplacement
            or AppShortcut.PasteLast or AppShortcut.ActionPalette) {
            // Dictation and the Translate shortcuts take one to three keys (e.g. Ctrl+Space);
            // they are saved once every key of the combination has been released.
            _host.Hotkey.CaptureMaxKeys = HotkeyChord.MaxKeys;
            _host.Hotkey.ChordCaptured += OnChordCaptured;
            _host.Hotkey.ChordReleased += OnChordReleased;
        } else {
            _host.Hotkey.CaptureMaxKeys = 1;
            _host.Hotkey.KeyCaptured += OnKeyCaptured;
        }
        _host.Hotkey.CaptureKeys = true;
    }

    private void StopCapture() {
        if (_capturing != null) {
            _capturing.Capturing = false;
            _capturing = null;
        }
        _host.Hotkey.CaptureKeys = false;
        _host.Hotkey.CaptureMaxKeys = 1;
        _host.Hotkey.KeyCaptured -= OnKeyCaptured;
        _host.Hotkey.ChordCaptured -= OnChordCaptured;
        _host.Hotkey.ChordReleased -= OnChordReleased;
        _translateKey.CapturePreview = null;
        _translateKey.Value = HotkeyNames.For(_host.Settings.TranslateChord);
        _translateKey.Invalidate();
        _translateSelectionKey.CapturePreview = null;
        _translateSelectionKey.Value = HotkeyNames.For(_host.Settings.TranslateSelectionChord);
        _translateSelectionKey.Invalidate();
        _addWordKey.CapturePreview = null;
        _addWordKey.Value = HotkeyNames.For(_host.Settings.AddWordReplacementChord);
        _addWordKey.Invalidate();
        _dictationKey.CapturePreview = null;
        _dictationKey.Value = HotkeyNames.For(_host.Settings.DictationChord);
        _dictationKey.Invalidate();
        _secondDictationKey.CapturePreview = null;
        _secondDictationKey.Value = HotkeyNames.For(_host.Settings.SecondDictationChord);
        _secondDictationKey.Invalidate();
        _pasteLastKey.CapturePreview = null;
        _pasteLastKey.Value = HotkeyNames.For(_host.Settings.PasteLastChord);
        _pasteLastKey.Invalidate();
        _paletteKey.CapturePreview = null;
        _paletteKey.Value = HotkeyNames.For(_host.Settings.ActionPaletteChord);
        _paletteKey.Invalidate();
    }

    private void OnChordCaptured(HotkeyChord chord) {
        if (IsDisposed || _capturing is not { MultiKey: true } row || chord.IsEmpty) {
            return;
        }
        row.CapturePreview = HotkeyNames.For(chord);
        row.Invalidate();
    }

    private void OnChordReleased(HotkeyChord chord) {
        if (IsDisposed || _capturing is not { MultiKey: true, Tag: AppShortcut shortcut }) {
            return;
        }
        StopCapture();
        if (chord.IsEmpty) {
            return;
        }
        if (shortcut == AppShortcut.Dictation) {
            _host.TrySetDictationChord(chord);
        } else if (shortcut == AppShortcut.SecondDictation) {
            _host.TrySetSecondDictationChord(chord);
        } else if (shortcut == AppShortcut.TranslateSelection) {
            _host.TrySetTranslateSelectionChord(chord);
        } else if (shortcut == AppShortcut.AddWordReplacement) {
            _host.TrySetAddWordReplacementChord(chord);
        } else if (shortcut == AppShortcut.PasteLast) {
            _host.TrySetPasteLastChord(chord);
        } else if (shortcut == AppShortcut.ActionPalette) {
            _host.TrySetActionPaletteChord(chord);
        } else {
            _host.TrySetTranslateChord(chord);
        }
    }

    private void OnKeyCaptured(uint vk) {
        if (IsDisposed || _capturing is not { Tag: AppShortcut shortcut }) {
            return;
        }
        var row = _capturing;
        StopCapture();
        if (vk == 0 || vk == NativeMethods.VK_ESCAPE) {
            return;
        }
        if (!_host.TrySetShortcut(shortcut, (int)vk)) {
            row.Capturing = false;
        }
    }

    private ComboBox MakeLanguageCombo(Action<string> onPick) {
        var combo = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Standard,
            MaxDropDownItems = 12,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 28,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = HudTheme.BodyText,
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        combo.DrawItem += OnLanguageDrawItem;
        foreach (var (code, name) in GoogleTranslation.Languages) {
            combo.Items.Add(new LanguageItem(code, name));
        }
        combo.SelectedIndexChanged += (_, _) => {
            if (!_syncing && combo.SelectedItem is LanguageItem item) {
                onPick(item.Code);
            }
        };
        return combo;
    }

    private void OnLanguageDrawItem(object? sender, DrawItemEventArgs e) {
        if (e.Index < 0 || sender is not ComboBox combo) {
            return;
        }
        // The hovered list item gets a soft well; the closed box and the rest stay white.
        bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var fill = new SolidBrush(hot ? HudTheme.Well : HudTheme.Surface)) {
            e.Graphics.FillRectangle(fill, e.Bounds);
        }
        var text = Rectangle.Inflate(e.Bounds, -HudTheme.Dip(this, 8), 0);
        TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString(), combo.Font, text,
            HudTheme.Ink,
            Loc.Flags(TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix));
    }

    private static int IndexOfLanguage(ComboBox combo, string code) {
        for (int i = 0; i < combo.Items.Count; i++) {
            if (combo.Items[i] is LanguageItem item
                && string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }
        return -1;
    }

    private sealed record LanguageItem(string Code, string Name) {
        public override string ToString() => Name;
    }

    // ── Health ────────────────────────────────────────────

    private void SyncHealth(bool relayout = true) {
        IReadOnlyList<HealthItem> items;
        try {
            items = _host.Health;
        } catch (Exception ex) {
            Log.Write("health: refresh failed: " + ex.Message);
            return;
        }
        bool changed = false;
        foreach (var (id, row) in _healthRows) {
            var item = items.FirstOrDefault(h => h.Id == id);
            changed |= SetCollapsed(row, item == null, relayout: false);
            if (item == null) {
                continue;
            }
            row.Title = item.Title;
            row.Description = item.Status;
            row.DescriptionColor = item.Level switch {
                HealthLevel.Ok => HudTheme.Positive,
                HealthLevel.Error => HudTheme.Negative,
                HealthLevel.Warning => HudTheme.Caution,
                _ => null,
            };
            var button = _healthButtons[id];
            button.Visible = item.FixLabel != null;
            if (item.FixLabel != null) {
                button.Text = item.FixLabel;
            }
            row.Invalidate();
        }
        if (changed && relayout) {
            Relayout();
        }
    }

    /// <summary>Hides or shows a row inside its card; true when that changed.</summary>
    private bool SetCollapsed(Control row, bool collapsed, bool relayout = true) {
        bool changed = collapsed ? _collapsed.Add(row) : _collapsed.Remove(row);
        if (changed && relayout && !_layingOut) {
            Relayout();
        }
        return changed;
    }

    /// <summary>Every shortcut that is set, for the overview on the Shortcuts page.</summary>
    private static List<(string Name, string Shortcut)> ShortcutOverview(Settings s) {
        var list = new List<(string, string)>();
        void Chord(string name, HotkeyChord chord) {
            if (!chord.IsEmpty) list.Add((Loc.T(name), HotkeyNames.For(chord)));
        }
        void Key(string name, int vk) {
            if (vk > 0) list.Add((Loc.T(name), HotkeyNames.For(vk)));
        }
        Chord("Dictation", s.DictationChord);
        Chord("Second dictation key", s.SecondDictationChord);
        Chord("Action palette", s.ActionPaletteChord);
        Key("Ask model", s.AskModelVk);
        Chord("Translate speech", s.TranslateChord);
        Chord("Translate selection", s.TranslateSelectionChord);
        Chord("Add word replacement", s.AddWordReplacementChord);
        Chord("Paste last result", s.PasteLastChord);
        Key("Auto Enter on/off", s.PressEnterToggleVk);
        Key("Auto Clean on/off", s.AutoCleanToggleVk);
        Key("Switch to ChatGPT", s.ChatGptSwitchVk);
        Key("Switch to Gemini", s.GeminiSwitchVk);
        Key("Switch to Claude", s.ClaudeSwitchVk);
        Key("Open model window", s.OpenModelWindowVk);
        Key("ChatGPT window", s.OpenChatGptWindowVk);
        Key("Gemini window", s.OpenGeminiWindowVk);
        Key("Claude window", s.OpenClaudeWindowVk);
        foreach (var cmd in s.ActiveCommands) {
            if (!cmd.Chord.IsEmpty) list.Add((cmd.DisplayName, HotkeyNames.For(cmd.Chord)));
        }
        return list;
    }

    // ── Search ────────────────────────────────────────────

    private int SearchWidth() => HudTheme.Dip(this, 240);

    private void PlaceSearch() {
        if (_search == null) {
            return;
        }
        int w = SearchWidth();
        int pad = HudTheme.Dip(this, PagePadDip);
        var r = new Rectangle(_header.Width - pad - w, HudTheme.Dip(this, 32), w, _search.Height);
        _search.Bounds = Loc.Mirror(r, _header.Width);
    }

    private void UpdateSearch() {
        string q = _search.Text.Trim();
        if (q.Length == 0) {
            _searchResults.Visible = false;
            return;
        }
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _searchShown = _searchIndex
            .Where(e => words.All(w => (e.Title + " " + e.Description + " " + _pages[e.Page].Title)
                .Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .GroupBy(e => e.Row).Select(g => g.First())
            .Take(12)
            .ToList();
        _searchResults.BeginUpdate();
        _searchResults.Items.Clear();
        foreach (var e in _searchShown) {
            _searchResults.Items.Add(e.Title + "   ·   " + _pages[e.Page].Title);
        }
        if (_searchShown.Count == 0) {
            _searchResults.Items.Add(Loc.T("No matching settings"));
        }
        _searchResults.EndUpdate();
        int itemH = _searchResults.ItemHeight;
        int w = HudTheme.Dip(this, 360);
        var origin = PointToClient(_header.PointToScreen(_search.Location));
        int x = Loc.Rtl ? origin.X : origin.X + _search.Width - w;
        _searchResults.Bounds = new Rectangle(Math.Max(0, x), origin.Y + _search.Height + 2, w,
            Math.Min(12, Math.Max(1, _searchResults.Items.Count)) * itemH + 4);
        _searchResults.Visible = true;
        _searchResults.BringToFront();
        if (_searchShown.Count > 0) {
            _searchResults.SelectedIndex = 0;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e) {
        if (!_searchResults.Visible) {
            return;
        }
        if (e.KeyCode == Keys.Down && _searchShown.Count > 0) {
            _searchResults.SelectedIndex = Math.Min(_searchShown.Count - 1, _searchResults.SelectedIndex + 1);
            e.Handled = true;
        } else if (e.KeyCode == Keys.Up && _searchShown.Count > 0) {
            _searchResults.SelectedIndex = Math.Max(0, _searchResults.SelectedIndex - 1);
            e.Handled = true;
        } else if (e.KeyCode == Keys.Enter) {
            JumpTo(Math.Max(0, _searchResults.SelectedIndex));
            e.Handled = e.SuppressKeyPress = true;
        } else if (e.KeyCode == Keys.Escape) {
            _search.Text = "";
            _searchResults.Visible = false;
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    /// <summary>Opens the page of search result <paramref name="index"/> and scrolls its row into view.</summary>
    private void JumpTo(int index) {
        if (index < 0 || index >= _searchShown.Count) {
            return;
        }
        var entry = _searchShown[index];
        _searchResults.Visible = false;
        ShowPage(entry.Page);
        _scrollHost.ScrollControlIntoView(entry.Row);
        if (entry.Row is HudSettingRow row && row.Controls.Count > 0 && row.Controls[0].CanFocus) {
            row.Controls[0].Focus();
        } else if (entry.Row.CanFocus) {
            entry.Row.Focus();
        }
    }

    // ── Number inputs ─────────────────────────────────────

    private NumericUpDown MakeNumber(int min, int max, int step, Action<int> changed) {
        var box = new NumericUpDown {
            Minimum = min,
            Maximum = max,
            Increment = step,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = HudTheme.Surface,
            ForeColor = HudTheme.Ink,
            Font = HudTheme.BodyText,
            TextAlign = HorizontalAlignment.Right,
        };
        box.ValueChanged += (_, _) => {
            if (!_syncing) {
                changed((int)box.Value);
            }
        };
        return box;
    }

    private static void SetNumberSilent(NumericUpDown box, int value) {
        if (!box.Focused) {
            box.Value = Math.Clamp(value, (int)box.Minimum, (int)box.Maximum);
        }
    }

    private static string HotkeyLabel(int vk) => vk <= 0 ? Loc.T("(none)") : HotkeyNames.For(vk);
}
