using System.Diagnostics;
using EchoType.Audio;
using EchoType.Hotkey;
using EchoType.Input;
using EchoType.Native;
using EchoType.Translation;
using EchoType.Ui;
using EchoType.Web;

namespace EchoType;

/// <summary>
/// Owns the tray icon, the global hotkey, and the dictation state machine
/// (AppDelegate.swift analog). Recording is hold-to-talk by default, or
/// press-to-start / press-to-stop when that mode is selected in the tray.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext, IAppWindowHost {

    /// <summary>Releases shorter than this are a tap → cancel (mac tapThreshold).</summary>
    private const double TapThresholdSeconds = 0.35;
    /// <summary>
    /// ChatGPT custom-command / ask-model replies stay on one web thread for this
    /// many successful sends, then rotate. Gemini always opens a fresh chat.
    /// </summary>
    private const int ChatGptThreadReuseLimit = 25;
    /// <summary>
    /// Short ChatGPT recordings wait this long for the first reply token before
    /// opening a new chat. Longer clips scale up from here.
    /// </summary>
    private const double MinChatGptFirstReplySeconds = 30;
    /// <summary>
    /// Cap for long recordings (two minutes or more) so a hung ChatGPT thread is
    /// retried within a minute instead of sitting idle.
    /// </summary>
    private const double MaxChatGptFirstReplySeconds = 60;
    private static readonly TimeSpan AutoCleanMaxWait = TimeSpan.FromSeconds(60);

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _loginItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _commandsRoot;
    private readonly ToolStripMenuItem _chatgptModelItem;
    private readonly ToolStripMenuItem _geminiModelItem;
    private readonly ToolStripMenuItem _claudeModelItem;
    private readonly ToolStripMenuItem _modelShortcutsItem;
    private readonly ToolStripMenuItem _pressEnterItem;
    private readonly ToolStripMenuItem _pressEnterShortcutItem;
    private readonly ToolStripMenuItem _askModelShortcutItem;
    private readonly ToolStripMenuItem _translateShortcutItem;
    private readonly ToolStripMenuItem _translateSelectionShortcutItem;
    private readonly ToolStripMenuItem _translateLanguageRoot;
    private readonly ToolStripMenuItem _translateSelectionLanguageRoot;
    private readonly ToolStripMenuItem _modelWindowShortcutItem;
    private readonly ToolStripMenuItem _muteOthersItem;
    private readonly ToolStripMenuItem _longDictationItem;
    private readonly ToolStripMenuItem _recordingModeRoot;
    private readonly ToolStripMenuItem _holdModeItem;
    private readonly ToolStripMenuItem _toggleModeItem;
    private readonly Control _marshal = new(); // UI-thread marshal target for the hook
    private readonly Settings _settings;
    private readonly ChatGPTWebController _web;
    /// <summary>
    /// Second hidden window that runs Auto Clean, custom commands and selection rewrites
    /// so dictation and cleaning never wait on each other. Null when the cleaning
    /// model is the same as the transcription model (then <see cref="_web"/> does both).
    /// </summary>
    private ChatGPTWebController? _cleanWeb;
    /// <summary>
    /// Third hidden window that answers Ask model. Null when the Ask model is the same as
    /// the transcription or cleaning model (that window answers instead).
    /// </summary>
    private ChatGPTWebController? _askWeb;
    private readonly ToolStripMenuItem _chatgptAskItem;
    private readonly ToolStripMenuItem _geminiAskItem;
    private readonly ToolStripMenuItem _claudeAskItem;
    private int _askWarmupEpoch;
    private readonly ToolStripMenuItem _autoCleanItem;
    private readonly ToolStripMenuItem _chatgptCleanItem;
    private readonly ToolStripMenuItem _geminiCleanItem;
    private readonly ToolStripMenuItem _claudeCleanItem;
    private int _cleanWarmupEpoch;
    private bool _cleanPromptUiOpen;
    private bool _openAiKeyUiOpen;
    private bool _wordReplacementsUiOpen;
    private bool _workspaceModesUiOpen;
    private bool _appProfilesUiOpen;
    private readonly HotkeyMonitor _hotkey;
    private readonly BackgroundAudioMuter _audioMuter = new();
    private readonly MicLevelMeter _micMeter = new();
    private AppWindow? _window;

    public event Action? UiChanged;

    private AppPhase _phase = AppPhase.Idle;
    /// <summary>Bumped when a session starts or is abandoned so leftover awaits do not paste.</summary>
    private int _session;
    private bool _hotkeyHeld;
    private DateTime _holdStartedAt;
    private DateTime _releasedAt;
    private DateTime _dictationKeyDownAt;
    private bool _loggedIn = true;
    private bool _online = true;
    private int _engagementFailures; // consecutive; 2+ triggers a page reload
    private CustomCommand? _activeCommand;
    private CommandButtonPickerForm? _actionPicker;
    private bool _askModel;
    /// <summary>Image files named by the selection (Obsidian embeds, paths) to attach to the Ask model prompt.</summary>
    private IReadOnlyList<string> _askImages = [];
    private bool _translate;
    /// <summary>Set while the Translate selection shortcut copies and translates a selection.</summary>
    private CancellationTokenSource? _selectionTranslateCts;
    private HotkeyChord _sessionChord;
    private PasteTarget? _pasteTarget;
    /// <summary>The <see cref="AppProfile"/> for the app the session started in, or null for the global settings.</summary>
    private AppProfile? _profile;
    private string? _selectedText;
    private Task<string?> _selectionTask = Task.FromResult<string?>(null);
    private bool _commandsUiOpen;
    private CancellationTokenSource? _listenWatchCts;
    private CancellationTokenSource? _micLevelCts;
    private int _listenFinishGate;
    /// <summary>Long dictation: transcripts of the pieces the site already cut off this session.</summary>
    private readonly List<string> _longPieces = [];
    /// <summary>Long dictation: set while a cut-off piece is collected and the page's dictation restarted.</summary>
    private bool _longContinuing;
    private int _longEmptyPieces; // consecutive; 3 stops restarting
    private DateTime _pieceStartedAt;
    private int _warmupEpoch;

    public TrayAppContext() {
        _settings = Settings.Load();
        // Before any font or window exists: HudTheme builds its fonts from the language.
        Loc.Apply(_settings.UiLanguage);
        // keepStandby: cancel swaps to a second preloaded page instead of waiting on a reload.
        _web = new ChatGPTWebController(ChatSite.For(_settings.TranscriptionProvider), keepStandby: true);
        _hotkey = new HotkeyMonitor();

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _loginItem = new ToolStripMenuItem(Loc.T("Open login…"), null, (_, _) => _web.ShowLoginWindow());
        var closeLoginItem = new ToolStripMenuItem(Loc.T("Close login window"), null, (_, _) => _web.HideLoginWindow());
        _chatgptModelItem = new ToolStripMenuItem("ChatGPT", null, (_, _) => SelectModel(TranscriptionProvider.ChatGpt));
        _geminiModelItem = new ToolStripMenuItem("Gemini", null, (_, _) => SelectModel(TranscriptionProvider.Gemini));
        _claudeModelItem = new ToolStripMenuItem("Claude", null, (_, _) => SelectModel(TranscriptionProvider.Claude));
        _modelShortcutsItem = new ToolStripMenuItem();
        _modelShortcutsItem.Click += (_, _) => OpenModelShortcutsUi();
        var modelRoot = new ToolStripMenuItem(Loc.T("Transcription model"));
        modelRoot.DropDownItems.Add(_chatgptModelItem);
        modelRoot.DropDownItems.Add(_geminiModelItem);
        modelRoot.DropDownItems.Add(_claudeModelItem);
        modelRoot.DropDownItems.Add(new ToolStripSeparator());
        modelRoot.DropDownItems.Add(_modelShortcutsItem);
        _autoCleanItem = new ToolStripMenuItem(Loc.T("Auto Clean dictation")) {
            CheckOnClick = true,
            Checked = _settings.AutoClean,
        };
        _autoCleanItem.CheckedChanged += (_, _) => SetAutoClean(_autoCleanItem.Checked);
        _chatgptCleanItem = new ToolStripMenuItem("ChatGPT", null, (_, _) => SetCleanModel(TranscriptionProvider.ChatGpt));
        _geminiCleanItem = new ToolStripMenuItem("Gemini", null, (_, _) => SetCleanModel(TranscriptionProvider.Gemini));
        _claudeCleanItem = new ToolStripMenuItem("Claude", null, (_, _) => SetCleanModel(TranscriptionProvider.Claude));
        var cleanRoot = new ToolStripMenuItem(Loc.T("Cleaning model (Auto Clean + commands)"));
        cleanRoot.DropDownItems.Add(_chatgptCleanItem);
        cleanRoot.DropDownItems.Add(_geminiCleanItem);
        cleanRoot.DropDownItems.Add(_claudeCleanItem);
        cleanRoot.DropDownItems.Add(new ToolStripSeparator());
        cleanRoot.DropDownItems.Add(Loc.T("Open cleaning model window…"), null, (_, _) => ToggleCleanWindow());
        cleanRoot.DropDownItems.Add(Loc.T("Edit cleaning prompt…"), null, (_, _) => EditCleanPrompt());
        _chatgptAskItem = new ToolStripMenuItem("ChatGPT", null, (_, _) => SetAskModel(TranscriptionProvider.ChatGpt));
        _geminiAskItem = new ToolStripMenuItem("Gemini", null, (_, _) => SetAskModel(TranscriptionProvider.Gemini));
        _claudeAskItem = new ToolStripMenuItem("Claude", null, (_, _) => SetAskModel(TranscriptionProvider.Claude));
        var askRoot = new ToolStripMenuItem(Loc.T("Ask model (answers the Ask shortcut)"));
        askRoot.DropDownItems.Add(_chatgptAskItem);
        askRoot.DropDownItems.Add(_geminiAskItem);
        askRoot.DropDownItems.Add(_claudeAskItem);
        askRoot.DropDownItems.Add(new ToolStripSeparator());
        askRoot.DropDownItems.Add(Loc.T("Open Ask model window…"), null, (_, _) => ToggleAskWindow());
        _commandsRoot = new ToolStripMenuItem(Loc.T("Custom Commands"));
        _pressEnterItem = new ToolStripMenuItem(Loc.T("Press Enter after paste")) {
            CheckOnClick = true,
            Checked = _settings.PressEnterAfterPaste,
        };
        _pressEnterItem.CheckedChanged += OnPressEnterAfterPasteChanged;
        _pressEnterShortcutItem = new ToolStripMenuItem();
        _pressEnterShortcutItem.Click += (_, _) => OpenPressEnterShortcutUi();
        UpdatePressEnterShortcutMenu();
        _askModelShortcutItem = new ToolStripMenuItem();
        _askModelShortcutItem.Click += (_, _) => OpenAskModelShortcutUi();
        UpdateAskModelShortcutMenu();
        _translateShortcutItem = new ToolStripMenuItem();
        _translateShortcutItem.Click += (_, _) => OpenTranslateShortcutUi();
        _translateSelectionShortcutItem = new ToolStripMenuItem();
        _translateSelectionShortcutItem.Click += (_, _) => OpenTranslateSelectionShortcutUi();
        _translateLanguageRoot = new ToolStripMenuItem();
        _translateSelectionLanguageRoot = new ToolStripMenuItem();
        foreach (var (code, name) in GoogleTranslation.Languages) {
            string c = code;
            _translateLanguageRoot.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SetTranslateLanguage(c)) {
                Tag = c,
            });
            _translateSelectionLanguageRoot.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SetTranslateSelectionLanguage(c)) {
                Tag = c,
            });
        }
        UpdateTranslateMenu();
        _modelWindowShortcutItem = new ToolStripMenuItem();
        _modelWindowShortcutItem.Click += (_, _) => OpenModelWindowShortcutUi();
        UpdateModelWindowShortcutMenu();
        _muteOthersItem = new ToolStripMenuItem(Loc.T("Pause or mute other apps while dictating")) {
            CheckOnClick = true,
            Checked = _settings.MuteOtherAppsWhileDictating,
        };
        _muteOthersItem.CheckedChanged += OnMuteOthersChanged;
        _longDictationItem = new ToolStripMenuItem(Loc.T("Long dictation (continue when the model cuts off)")) {
            CheckOnClick = true,
            Checked = _settings.LongDictation,
        };
        _longDictationItem.CheckedChanged += OnLongDictationChanged;
        _holdModeItem = new ToolStripMenuItem(Loc.T("Hold to talk"), null, (_, _) => SetToggleRecording(false));
        _toggleModeItem = new ToolStripMenuItem(Loc.T("Press to start/stop"), null, (_, _) => SetToggleRecording(true));
        _recordingModeRoot = new ToolStripMenuItem(Loc.T("Recording mode"));
        _recordingModeRoot.DropDownItems.Add(_holdModeItem);
        _recordingModeRoot.DropDownItems.Add(_toggleModeItem);
        SyncRecordingModeMenu();

        // One submenu per topic, in the same order as the main window's sidebar pages.
        var dictationRoot = new ToolStripMenuItem(Loc.T("Dictation"));
        dictationRoot.DropDownItems.Add(modelRoot);
        dictationRoot.DropDownItems.Add(_recordingModeRoot);
        dictationRoot.DropDownItems.Add(_muteOthersItem);
        dictationRoot.DropDownItems.Add(_longDictationItem);
        dictationRoot.DropDownItems.Add(new ToolStripSeparator());
        dictationRoot.DropDownItems.Add(_loginItem);
        dictationRoot.DropDownItems.Add(closeLoginItem);
        dictationRoot.DropDownItems.Add(_modelWindowShortcutItem);
#if DEBUG
        dictationRoot.DropDownItems.Add(new ToolStripSeparator());
        dictationRoot.DropDownItems.Add(new ToolStripMenuItem("Start dictation (debug)", null, (_, _) => DebugStart()));
#endif

        var cleanAskRoot = new ToolStripMenuItem(Loc.T("Clean & Ask"));
        cleanAskRoot.DropDownItems.Add(_autoCleanItem);
        cleanAskRoot.DropDownItems.Add(cleanRoot);
        cleanAskRoot.DropDownItems.Add(new ToolStripSeparator());
        cleanAskRoot.DropDownItems.Add(askRoot);
        cleanAskRoot.DropDownItems.Add(_askModelShortcutItem);

        var translateRoot = new ToolStripMenuItem(Loc.T("Translate"));
        translateRoot.DropDownItems.Add(_translateLanguageRoot);
        translateRoot.DropDownItems.Add(_translateShortcutItem);
        translateRoot.DropDownItems.Add(new ToolStripSeparator());
        translateRoot.DropDownItems.Add(_translateSelectionLanguageRoot);
        translateRoot.DropDownItems.Add(_translateSelectionShortcutItem);
        translateRoot.DropDownItems.Add(new ToolStripSeparator());
        translateRoot.DropDownItems.Add(Loc.T("OpenAI API key (translation)…"), null, (_, _) => EditOpenAiKey());

        var commandsRoot = new ToolStripMenuItem(Loc.T("Commands"));
        commandsRoot.DropDownItems.Add(_commandsRoot);
        commandsRoot.DropDownItems.Add(Loc.T("Workspace modes…"), null, (_, _) => EditWorkspaceModes());
        commandsRoot.DropDownItems.Add(Loc.T("Word replacements…"), null, (_, _) => EditWordReplacements());

        var pasteRoot = new ToolStripMenuItem(Loc.T("Paste"));
        pasteRoot.DropDownItems.Add(_pressEnterItem);
        pasteRoot.DropDownItems.Add(_pressEnterShortcutItem);
        pasteRoot.DropDownItems.Add(new ToolStripSeparator());
        pasteRoot.DropDownItems.Add(Loc.T("Per-app profiles…"), null, (_, _) => EditAppProfiles());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(Loc.T("Open EchoType"), null, (_, _) => ShowAppWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(dictationRoot);
        menu.Items.Add(cleanAskRoot);
        menu.Items.Add(translateRoot);
        menu.Items.Add(commandsRoot);
        menu.Items.Add(pasteRoot);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.T("Open log"), null, (_, _) => OpenLog());
        menu.Items.Add(Loc.T("Quit EchoType"), null, (_, _) => ExitThread());
        Loc.ApplyTo(menu);
        RebuildCommandsMenu();
        SyncModelMenu();
        SyncCleanMenu();
        SyncAskMenu();
        UpdateModelShortcutsMenu();
        UpdateLoginMenuItem();
        _statusItem.Text = StatusLine();

        _tray = new NotifyIcon {
            Icon = TrayIcons.For(_phase, _loggedIn, _online, pageReady: false),
            Text = "EchoType — " + StatusLine(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowAppWindow();

        _web.LoginStateChanged += OnLoginStateChanged;
        _web.ReachabilityChanged += OnReachabilityChanged;
        _web.InteractiveChanged += OnInteractiveChanged;
        _web.MissingUiPartChanged += OnMissingUiPartChanged;

        // BeginInvoke on this control is how the keyboard hook reaches the UI
        // thread. A Control with no HWND throws, which kills the hook — Right
        // Ctrl then appears to do nothing. Force the handle before installing.
        _ = _marshal.Handle;

        DropTranslateSelectionChordIfTaken();
        if (!_hotkey.Start(CollectHotkeys(), CollectTapHotkeys(), _marshal)) {
            MessageBox.Show(Loc.T("EchoType could not install its global keyboard hook."), "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _hotkey.HoldStart += OnHoldStart;
        _hotkey.HoldEnd += OnHoldEnd;
        _hotkey.TapPressed += OnTapPressed;
        _hotkey.CancelRequested += OnCancelRequested;
        StatusOverlay.CancelClicked += OnHudCancelClicked;

        Log.Write($"launch: EchoType for Windows started (model={_web.Site.Id}, hotkey={HotkeyNames.For(_settings.DictationChord)}, askModelVk=0x{_settings.AskModelVk:X2}, askModel={_settings.AskProviderName}, translate={HotkeyNames.For(_settings.TranslateChord)}, translateSelection={HotkeyNames.For(_settings.TranslateSelectionChord)}, translateTo={_settings.TranslateTargetLanguage}, translateSelectionTo={_settings.TranslateSelectionTargetLanguage}, openModelWindowVk=0x{_settings.OpenModelWindowVk:X2}, commands={_settings.ActiveCommands.Count}, toggleRecording={_settings.ToggleRecording}, pressEnterAfterPaste={_settings.PressEnterAfterPaste}, pressEnterToggleVk=0x{_settings.PressEnterToggleVk:X2}, chatgptSwitchVk=0x{_settings.ChatGptSwitchVk:X2}, geminiSwitchVk=0x{_settings.GeminiSwitchVk:X2}, muteOtherAppsWhileDictating={_settings.MuteOtherAppsWhileDictating}, longDictation={_settings.LongDictation})");
        StartupRegistration.Apply(_settings.StartWithWindows);
        // Stay in the tray on launch. Double-click the icon (or Open EchoType) to show the window.
        _ = WarmupAsync(); // alwaysReady: load the selected model at launch (mac applyPolicyAtLaunch)
        ApplyReplySites(); // windows for the cleaning and Ask models, warmed in parallel
    }

    // ------------------------------------------------------------------
    // Cleaning model (second window)
    // ------------------------------------------------------------------

    private void SyncCleanMenu() {
        _autoCleanItem.Checked = _settings.AutoClean;
        _chatgptCleanItem.Checked = _settings.CleanProvider == TranscriptionProvider.ChatGpt;
        _geminiCleanItem.Checked = _settings.CleanProvider == TranscriptionProvider.Gemini;
        _claudeCleanItem.Checked = _settings.CleanProvider == TranscriptionProvider.Claude;
    }

    /// <summary>
    /// Keeps the second window in step with settings: a separate hidden window when the
    /// cleaning model differs from the transcription model, otherwise the single window does both.
    /// </summary>
    private void ApplyCleanSite() {
        var provider = _settings.CleanProvider;
        if (provider == _settings.TranscriptionProvider) {
            if (_cleanWeb != null) {
                _cleanWeb.Dispose();
                _cleanWeb = null;
            }
        } else if (_cleanWeb == null) {
            _cleanWeb = new ChatGPTWebController(ChatSite.For(provider));
            WarmupCleanWindow();
        } else if (_cleanWeb.Site.Id != ChatSite.For(provider).Id) {
            _cleanWeb.SwitchSite(ChatSite.For(provider));
            WarmupCleanWindow();
        }
        SyncCleanMenu();
        NotifyUi();
    }

    private void WarmupCleanWindow() {
        var web = _cleanWeb;
        if (web == null) {
            return;
        }
        int epoch = ++_cleanWarmupEpoch;
        _ = WarmupSideWindowAsync(web, "clean",
            Loc.F("Log in to {0} (cleaning model) from the tray menu.", web.Site.DisplayName),
            () => epoch == _cleanWarmupEpoch && ReferenceEquals(web, _cleanWeb));
    }

    private static async Task WarmupSideWindowAsync(
        ChatGPTWebController web, string role, string loggedOutNotice, Func<bool> stillCurrent) {
        try {
            var outcome = await web.EnsureReadyAsync();
            if (!stillCurrent()) {
                return;
            }
            Log.Write("launch: " + role + " warmup (" + web.Site.Id + ") -> " + outcome);
            if (outcome == ReadyOutcome.RuntimeMissing) {
                ChatGPTWebController.ShowRuntimeMissingDialog();
            } else if (outcome == ReadyOutcome.LoggedOut) {
                ShowBalloon(loggedOutNotice, OverlayKind.Warning);
            }
        } catch (Exception ex) {
            Log.Write("launch: " + role + " warmup failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Ask model (third window)
    // ------------------------------------------------------------------

    /// <summary>
    /// Brings the cleaning and Ask windows in step with settings. An Ask window that is no
    /// longer needed is closed before the cleaning window changes site, so two windows never
    /// load the same model's profile at once.
    /// </summary>
    private void ApplyReplySites() {
        if (_askWeb != null && AskSharesWindow) {
            _askWeb.Dispose();
            _askWeb = null;
        }
        ApplyCleanSite();
        ApplyAskSite();
    }

    /// <summary>True when the transcription or cleaning window already runs the Ask model.</summary>
    private bool AskSharesWindow =>
        _settings.AskProvider == _settings.TranscriptionProvider || _settings.AskProvider == _settings.CleanProvider;

    private void ApplyAskSite() {
        var site = ChatSite.For(_settings.AskProvider);
        if (AskSharesWindow) {
            if (_askWeb != null) {
                _askWeb.Dispose();
                _askWeb = null;
            }
        } else if (_askWeb == null) {
            _askWeb = new ChatGPTWebController(site);
            WarmupAskWindow();
        } else if (_askWeb.Site.Id != site.Id) {
            _askWeb.SwitchSite(site);
            WarmupAskWindow();
        }
        SyncAskMenu();
        NotifyUi();
    }

    private void WarmupAskWindow() {
        var web = _askWeb;
        if (web == null) {
            return;
        }
        int epoch = ++_askWarmupEpoch;
        _ = WarmupSideWindowAsync(web, "ask",
            Loc.F("Log in to {0} (Ask model) from the tray menu.", web.Site.DisplayName),
            () => epoch == _askWarmupEpoch && ReferenceEquals(web, _askWeb));
    }

    private void SyncAskMenu() {
        _chatgptAskItem.Checked = _settings.AskProvider == TranscriptionProvider.ChatGpt;
        _geminiAskItem.Checked = _settings.AskProvider == TranscriptionProvider.Gemini;
        _claudeAskItem.Checked = _settings.AskProvider == TranscriptionProvider.Claude;
    }

    private void SetAskModel(TranscriptionProvider provider) {
        if (_settings.AskProvider == provider) {
            SyncAskMenu();
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon(Loc.T("Wait until dictation finishes before switching models."), OverlayKind.Warning);
            SyncAskMenu();
            return;
        }
        _settings.AskProvider = provider;
        _settings.Save();
        Log.Write("settings: askProvider=" + _settings.AskProviderName);
        ApplyReplySites();
        ShowBalloon(Loc.F("Ask model: {0}.", AskWeb.Site.DisplayName), OverlayKind.Success);
    }

    private void ToggleAskWindow() {
        var web = AskWeb;
        if (web.IsLoginWindowVisible) {
            web.HideLoginWindow();
        } else {
            web.ShowLoginWindow();
        }
        NotifyUi();
    }

    private void SetAutoClean(bool value) {
        if (_settings.AutoClean == value) {
            return;
        }
        _settings.AutoClean = value;
        _settings.Save();
        SyncCleanMenu();
        Log.Write("settings: autoClean=" + value);
        ShowBalloon(value ? Loc.F("Auto Clean on ({0}).", Gen.Site.DisplayName) : Loc.T("Auto Clean off."), OverlayKind.Info);
        NotifyUi();
    }

    private void SetCleanModel(TranscriptionProvider provider) {
        if (_settings.CleanProvider == provider) {
            SyncCleanMenu();
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon(Loc.T("Wait until dictation finishes before switching models."), OverlayKind.Warning);
            SyncCleanMenu();
            return;
        }
        _settings.CleanProvider = provider;
        _settings.Save();
        Log.Write("settings: cleanProvider=" + _settings.CleanProviderName);
        ApplyReplySites();
        RebuildCommandsMenu();
        ShowBalloon(Loc.F("Cleaning model: {0}.", Gen.Site.DisplayName), OverlayKind.Success);
    }

    private void EditOpenAiKey() {
        if (_openAiKeyUiOpen) {
            return;
        }
        _openAiKeyUiOpen = true;
        try {
            using var form = new OpenAiKeyForm(_settings.OpenAiApiKey, _settings.OpenAiTranslationModel);
            IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
            if (form.ShowDialog(owner) != DialogResult.OK || form.Result == _settings.OpenAiApiKey) {
                return;
            }
            _settings.OpenAiApiKey = form.Result;
            _settings.Save();
            Log.Write("settings: openAiApiKey " + (form.Result.Length == 0 ? "cleared" : "set"));
            ShowBalloon(form.Result.Length == 0
                ? "OpenAI key cleared — translation uses Google Translate."
                : "OpenAI key saved — translation uses OpenAI.", OverlayKind.Success);
            UpdateTranslateMenu();
            NotifyUi();
        } catch (Exception ex) {
            Log.Write("openai key ui: " + ex);
            MessageBox.Show("Could not open the OpenAI key settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _openAiKeyUiOpen = false;
        }
    }

    private void EditCleanPrompt() {
        if (_cleanPromptUiOpen) {
            return;
        }
        _cleanPromptUiOpen = true;
        try {
            using var form = new CleanPromptForm(_settings.AutoCleanPrompt, Gen.Site.DisplayName);
            IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
            if (form.ShowDialog(owner) != DialogResult.OK || form.Result == _settings.AutoCleanPrompt) {
                return;
            }
            _settings.AutoCleanPrompt = form.Result;
            _settings.Save();
            Log.Write("settings: autoCleanPrompt updated (" + form.Result.Length + " chars)");
            ShowBalloon(Loc.T("Auto Clean prompt saved."), OverlayKind.Success);
            NotifyUi();
        } catch (Exception ex) {
            Log.Write("clean prompt ui: " + ex);
            MessageBox.Show("Could not open the Auto Clean prompt: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _cleanPromptUiOpen = false;
        }
    }

    private string ApplyWordReplacements(string text) {
        if (_settings.WordReplacements.Count == 0) {
            return text;
        }
        string replaced = WordReplacement.Apply(text, _settings.WordReplacements).Trim();
        if (replaced != text) {
            Log.Write("word-replacements: applied (" + text.Length + " -> " + replaced.Length + " chars)");
        }
        return replaced;
    }

    private void EditWordReplacements() {
        if (_wordReplacementsUiOpen) {
            return;
        }
        _wordReplacementsUiOpen = true;
        try {
            using var form = new WordReplacementsForm(_settings.WordReplacements);
            IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
            if (form.ShowDialog(owner) != DialogResult.OK) {
                return;
            }
            _settings.WordReplacements = form.Result;
            _settings.Save();
            Log.Write("settings: wordReplacements=" + form.Result.Count);
            ShowBalloon(Loc.F("Word replacements saved ({0}).", form.Result.Count), OverlayKind.Success);
            NotifyUi();
        } catch (Exception ex) {
            Log.Write("word replacements ui: " + ex);
            MessageBox.Show("Could not open Word replacements: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _wordReplacementsUiOpen = false;
        }
    }

    private void EditWorkspaceModes() {
        if (_workspaceModesUiOpen) {
            return;
        }
        _workspaceModesUiOpen = true;
        try {
            using var form = new WorkspaceModesForm(_settings.WorkspaceModes);
            IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
            if (form.ShowDialog(owner) != DialogResult.OK) {
                return;
            }
            _settings.WorkspaceModes = form.Result;
            _settings.Save();
            Log.Write("settings: workspaceModes=" + form.Result.Count);
            ShowBalloon(Loc.F("Workspace modes saved ({0}).", form.Result.Count), OverlayKind.Success);
            NotifyUi();
        } catch (Exception ex) {
            Log.Write("workspace modes ui: " + ex);
            MessageBox.Show("Could not open Workspace modes: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _workspaceModesUiOpen = false;
        }
    }

    private void EditAppProfiles() {
        if (_appProfilesUiOpen) {
            return;
        }
        _appProfilesUiOpen = true;
        try {
            using var form = new AppProfilesForm(_settings.AppProfiles, _settings.AutoCleanPrompt, Gen.Site.DisplayName);
            IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
            if (form.ShowDialog(owner) != DialogResult.OK) {
                return;
            }
            _settings.AppProfiles = form.Result;
            _settings.Save();
            Log.Write("settings: appProfiles=" + form.Result.Count);
            ShowBalloon(Loc.F("Per-app profiles saved ({0}).", form.Result.Count), OverlayKind.Success);
            NotifyUi();
        } catch (Exception ex) {
            Log.Write("app profiles ui: " + ex);
            MessageBox.Show("Could not open Per-app profiles: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _appProfilesUiOpen = false;
        }
    }

    /// <summary>
    /// A short dictation like "coding mode" presses that mode's shortcut instead of pasting, so
    /// the user's workspace app can switch. The transcript stays on the clipboard.
    /// </summary>
    private bool TryTriggerWorkspaceMode(string transcript) {
        if (_settings.WorkspaceModes.Count == 0) {
            return false;
        }
        var mode = WorkspaceMode.Match(transcript, _settings.WorkspaceModes);
        if (mode == null) {
            return false;
        }
        Log.Write("workspace-mode: \"" + mode.Name + "\" -> " + mode.Shortcut);
        CopyTranscript(transcript);
        if (WorkspaceMode.SendShortcut(mode.Shortcut)) {
            Sounds.Pasted();
            ShowBalloon(Loc.F("{0} — pressed {1}.", mode.Name, mode.Shortcut), OverlayKind.Success);
        } else {
            Log.Write("workspace-mode: SendInput failed");
            Sounds.Error();
            ShowBalloon(Loc.F("Could not press {0} for {1}.", mode.Shortcut, mode.Name), OverlayKind.Error);
        }
        ResetToIdle();
        return true;
    }

    private void ToggleCleanWindow() {
        if (Gen.IsLoginWindowVisible) {
            Gen.HideLoginWindow();
        } else {
            Gen.ShowLoginWindow();
        }
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Hotkey → state machine
    // ------------------------------------------------------------------

    private void OnHoldStart(HotkeyChord chord) {
        if (_commandsUiOpen) {
            return;
        }
        if (IsTranslateSelectionChord(chord)) {
            _ = TranslateSelectionAsync();
            return;
        }
        bool isDictation = IsDictationChord(chord);
        if (isDictation && ConsumeCancelDoubleTap()) {
            // In toggle mode the second press of the same recording key stops
            // listening; don't treat that as the cancel double-tap.
            if (!(_settings.ToggleRecording && _phase == AppPhase.Listening && IsSessionChord(chord))) {
                if (_phase != AppPhase.Idle) {
                    _ = CancelAndResetAsync("double-tap, cancelled");
                }
                return;
            }
        }
        switch (_phase) {
            case AppPhase.Idle: {
                _hotkeyHeld = true;
                _sessionChord = chord;
                _holdStartedAt = DateTime.UtcNow;
                _releasedAt = default;
                _pasteTarget = PasteTarget.Capture();
                _profile = AppProfile.Match(_pasteTarget?.ProcessName, _settings.AppProfiles);
                if (_profile != null) {
                    Log.Write("app-profile: " + _pasteTarget!.ProcessName + " → " + _profile.DisplayName);
                }
                _activeCommand = FindCommand(chord);
                _askModel = _activeCommand == null
                    && !isDictation
                    && _settings.AskModelVk != 0
                    && chord.Count == 1
                    && chord.K1 == (uint)_settings.AskModelVk;
                _translate = _activeCommand == null
                    && !isDictation
                    && !_askModel
                    && !_settings.TranslateChord.IsEmpty
                    && chord.Equals(_settings.TranslateChord);
                _selectedText = null;
                _selectionTask = _pasteTarget != null
                    ? SelectionCapture.CaptureAsync()
                    : Task.FromResult<string?>(null);
                Log.Write(_activeCommand != null
                    ? $"command: key down ({_activeCommand.DisplayName}, {HotkeyNames.For(chord)})"
                    : _askModel
                        ? "ask-model: key down"
                        : _translate
                            ? "translate: key down"
                            : "dictation: key down");
                MuteOtherAppsIfEnabled();
                EnsureMicAtFullVolume();
                int session = ++_session;
                SetPhase(_web.HasWebView ? AppPhase.Engaging : AppPhase.Waking);
                _ = StartDictationSessionAsync(session);
                break;
            }
            case AppPhase.Waking:
            case AppPhase.Engaging:
                if (_settings.ToggleRecording && IsSessionChord(chord)) {
                    _ = CancelAndResetAsync("toggle press while opening, cancelled");
                    break;
                }
                _hotkeyHeld = true; // re-press before ready — keep waiting
                Log.Write("dictation: second press while opening");
                break;
            case AppPhase.Listening:
                if (_settings.ToggleRecording && IsSessionChord(chord)) {
                    StopToggleListening();
                    break;
                }
                Log.Write($"dictation: key down ignored, phase {_phase} busy");
                break;
            default:
                Log.Write($"dictation: key down ignored, phase {_phase} busy");
                break;
        }
    }

    private void OnHoldEnd(HotkeyChord chord) {
        if (IsTranslateSelectionChord(chord)) {
            return;
        }
        if (_settings.ToggleRecording) {
            // Release does not stop recording; the next press of the same key does.
            return;
        }
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        double held = HeldSeconds();

        switch (_phase) {
            case AppPhase.Listening when _longContinuing:
                // ContinueLongDictationAsync sees the release and finishes instead of restarting.
                Log.Write("dictation: released while long dictation restarts");
                break;
            case AppPhase.Listening:
                if (held >= TapThresholdSeconds) {
                    _ = FinishListeningAsync(_session);
                } else {
                    _ = CancelAndResetAsync("single tap, cancelled");
                }
                break;
            case AppPhase.Waking:
            case AppPhase.Engaging:
                Log.Write($"dictation: released while {_phase} (held {held:F2}s)");
                break;
        }
    }

    private void StopToggleListening() {
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        double held = HeldSeconds();
        Log.Write($"dictation: toggle stop (held {held:F2}s)");
        if (_longContinuing) {
            return; // ContinueLongDictationAsync finishes instead of restarting
        }
        if (held >= TapThresholdSeconds) {
            _ = FinishListeningAsync(_session);
        } else {
            _ = CancelAndResetAsync("toggle tap too short, cancelled");
        }
    }

    /// <summary>The ✕ on the listening or progress pill: abort, nothing is pasted.</summary>
    private void OnHudCancelClicked() {
        if (_phase != AppPhase.Idle) {
            _ = CancelAndResetAsync("cancelled with the HUD ✕");
        } else if (_selectionTranslateCts != null) {
            Log.Write("translate-selection: cancelled with the HUD ✕");
            _selectionTranslateCts.Cancel();
            StatusOverlay.HideWorking();
        }
    }

    private void OnCancelRequested() {
        // Physical Esc swallowed while Listening or choosing a command button.
        if (_phase is AppPhase.Listening or AppPhase.ChoosingAction) {
            _ = CancelAndResetAsync("cancelled with Esc");
        }
    }

    private void OnTapPressed(uint vk) {
        if (_commandsUiOpen) {
            return;
        }
        if (_settings.PressEnterToggleVk != 0 && vk == (uint)_settings.PressEnterToggleVk) {
            ApplyPressEnterAfterPaste(!_settings.PressEnterAfterPaste, announce: true);
            return;
        }
        if (_settings.AutoCleanToggleVk != 0 && vk == (uint)_settings.AutoCleanToggleVk) {
            SetAutoClean(!_settings.AutoClean);
            return;
        }
        if (_settings.ChatGptSwitchVk != 0 && vk == (uint)_settings.ChatGptSwitchVk) {
            SelectModel(TranscriptionProvider.ChatGpt);
            return;
        }
        if (_settings.GeminiSwitchVk != 0 && vk == (uint)_settings.GeminiSwitchVk) {
            SelectModel(TranscriptionProvider.Gemini);
            return;
        }
        if (_settings.ClaudeSwitchVk != 0 && vk == (uint)_settings.ClaudeSwitchVk) {
            SelectModel(TranscriptionProvider.Claude);
            return;
        }
        if (_settings.OpenModelWindowVk != 0 && vk == (uint)_settings.OpenModelWindowVk) {
            OpenModelWindow();
        }
    }

    private double HeldSeconds() {
        if (_holdStartedAt == default) {
            return 0;
        }
        DateTime end = _releasedAt == default ? DateTime.UtcNow : _releasedAt;
        return end.Subtract(_holdStartedAt).TotalSeconds;
    }

    /// <summary>
    /// Two presses of the dictation key (Right Ctrl by default) within the
    /// Windows double-click interval cancel the in-flight task.
    /// </summary>
    private bool ConsumeCancelDoubleTap() {
        DateTime now = DateTime.UtcNow;
        uint windowMs = NativeMethods.GetDoubleClickTime();
        if (windowMs == 0) {
            windowMs = 500;
        }
        bool isDouble = _dictationKeyDownAt != default
            && (now - _dictationKeyDownAt).TotalMilliseconds <= windowMs;
        _dictationKeyDownAt = isDouble ? default : now;
        return isDouble;
    }

    private bool IsLive(int session) => session == _session;

    private async Task StartDictationSessionAsync(int session) {
        var outcome = await _web.EnsureReadyAsync();
        if (!IsLive(session)) {
            return;
        }

        switch (outcome) {
            case ReadyOutcome.Ready:
                break;
            case ReadyOutcome.LoggedOut:
                _loggedIn = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                HandleFailure(new DriverException(DriverFailure.LoggedOut, "logged out"));
                return;
            case ReadyOutcome.Offline:
                HandleFailure(new DriverException(DriverFailure.Offline, "offline"));
                return;
            case ReadyOutcome.RuntimeMissing:
                ChatGPTWebController.ShowRuntimeMissingDialog();
                ResetToIdle();
                return;
            case ReadyOutcome.UiChanged:
                HandleFailure(ToDriverException(outcome));
                return;
            default:
                HandleFailure(new DriverException(
                    outcome == ReadyOutcome.Timeout ? DriverFailure.Timeout : DriverFailure.NotReady,
                    outcome.ToString()));
                return;
        }

        if (!_hotkeyHeld) {
            // Released while the page was still waking up — discard (mac parity).
            Log.Write("dictation: released before ready, discarded");
            ResetToIdle();
            return;
        }

        SetPhase(AppPhase.Engaging);
        await OpenMicrophoneAsync(session);
    }

    private async Task OpenMicrophoneAsync(int session) {
        if (!IsLive(session)) {
            return;
        }
        var driver = _web.Driver;
        if (driver == null) {
            HandleFailure(new DriverException(DriverFailure.NotReady, "no driver"));
            return;
        }
        try {
            await driver.StartDictationAsync();
        } catch (DriverException ex) {
            if (IsLive(session)) {
                HandleFailure(ex);
            }
            return;
        }
        if (!IsLive(session)) {
            return;
        }

        // Opening the mic is when Windows most often drops the input slider.
        EnsureMicAtFullVolume();
        _engagementFailures = 0;
        Sounds.Start();
        _longPieces.Clear();
        _longEmptyPieces = 0;
        _pieceStartedAt = default; // first piece is timed from key-down, like a normal take
        SetPhase(AppPhase.Listening);
        _listenFinishGate = 0;
        _hotkey.EscSwallowActive = true;
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

        if (_hotkeyHeld) {
            StartListenWatch(session);
            _ = RecheckMicVolumeSoonAsync(session);
            return; // normal hold — wait for release, or a model-side cutoff
        }

        // Key already released while we were starting (mac openMicrophone parity):
        // a long press finishes immediately, a short tap cancels.
        if (HeldSeconds() < TapThresholdSeconds) {
            await CancelAndResetAsync("released too early, cancelled");
        } else {
            await FinishListeningAsync(session);
        }
    }

    /// <param name="micOpen">
    /// False when Long dictation already collected the last piece and the page isn't recording,
    /// so only the collected pieces are delivered.
    /// </param>
    private async Task FinishListeningAsync(int session, bool micOpen = true) {
        if (!IsLive(session)) {
            return;
        }
        if (Interlocked.CompareExchange(ref _listenFinishGate, 1, 0) != 0) {
            return;
        }
        StopListenWatch();
        RestoreOtherApps();
        SetPhase(AppPhase.Transcribing);
        _hotkey.EscSwallowActive = false;
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);

        var driver = _web.Driver;
        if (driver == null) {
            HandleFailure(new DriverException(DriverFailure.NotReady, "no driver"));
            return;
        }

        string transcript = "";
        if (micOpen) {
            try {
                await driver.SubmitDictationAsync();
                if (!IsLive(session)) {
                    return;
                }
                transcript = await driver.AwaitTranscriptAsync(TimeSpan.FromSeconds(PieceSeconds()));
                if (!IsLive(session)) {
                    return;
                }
                await driver.ClearComposerAsync();
            } catch (DriverException ex) {
                if (!IsLive(session)) {
                    return;
                }
                string earlier = JoinLongPieces("");
                // mac collectTranscript failure parity: cancel + clear, then report.
                try { await driver.CancelDictationAsync(); } catch { /* best effort */ }
                try { await driver.ClearComposerAsync(); } catch { /* best effort */ }
                if (IsLive(session)) {
                    HandleFailure(ex, earlier.Length > 0 ? earlier : null);
                }
                return;
            }
            if (!IsLive(session)) {
                return;
            }
        }
        if (_longPieces.Count > 0) {
            Log.Write($"dictation: long dictation joined {_longPieces.Count + (transcript.Trim().Length > 0 ? 1 : 0)} pieces");
            transcript = JoinLongPieces(transcript);
        }

        try {
            _selectedText = await _selectionTask;
        } catch (Exception ex) {
            Log.Write("selection: await failed: " + ex.Message);
            _selectedText = null;
        }
        if (!IsLive(session)) {
            return;
        }
        if (!string.IsNullOrEmpty(_selectedText)) {
            Log.Write((_askModel ? "ask-model" : _translate ? "translate" : _activeCommand != null ? "command" : "dictation")
                + ": using selected " + _selectedText.Length + " chars");
        }

        transcript = ApplyWordReplacements(transcript);
        if (transcript.Length == 0) {
            Log.Write("dictation: empty transcript");
            Sounds.Error();
            ShowBalloon(_settings.ToggleRecording
                ? Loc.T("Nothing transcribed — try speaking longer before pressing the key again.")
                : Loc.T("Nothing transcribed — try holding the key longer."), OverlayKind.Error);
            ResetToIdle();
            return;
        }

        if (_activeCommand != null) {
            CopyTranscript(transcript);
            var buttons = _activeCommand.ResolvedButtons;
            CommandButton? chosen = buttons.Count == 1 ? buttons[0] : null;
            if (buttons.Count > 1) {
                chosen = await PickCommandButtonAsync(_activeCommand, buttons, session);
                if (!IsLive(session)) {
                    return;
                }
                if (chosen == null) {
                    Log.Write("command: action picker cancelled");
                    CopyTranscript(transcript);
                    ShowBalloon(Loc.T("Cancelled. Transcript copied to clipboard."), OverlayKind.Info);
                    ResetToIdle();
                    return;
                }
            }
            if (chosen == null) {
                Log.Write("command: no action buttons");
                Sounds.Error();
                ShowBalloon(Loc.T("This command has no buttons. Edit it from Custom Commands."), OverlayKind.Error);
                ResetToIdle();
                return;
            }
            string label = buttons.Count > 1
                ? "command (" + _activeCommand.DisplayName + " / " + chosen.DisplayName + ")"
                : "command (" + _activeCommand.DisplayName + ")";
            await DeliverGeneratedReplyAsync(
                _activeCommand.BuildMessage(transcript, _selectedText, chosen),
                label,
                session,
                transcript);
            return;
        }

        if (_translate) {
            await DeliverTranslationAsync(transcript, session);
            return;
        }

        if (_askModel) {
            CopyTranscript(transcript);
            // A selected image path or Obsidian embed (![[image.png]]) is attached as the
            // image itself; any other selected text still goes in with the question.
            string? selection = _selectedText;
            if (!string.IsNullOrEmpty(selection)) {
                var refs = ImageReferences.Extract(selection);
                if (refs.Paths.Count > 0) {
                    _askImages = refs.Paths;
                    selection = refs.Remainder;
                    Log.Write("ask-model: attaching " + refs.Paths.Count + " image(s) from the selection");
                }
            }
            string message = string.IsNullOrEmpty(selection)
                ? transcript
                : SelectionCapture.BuildAskModelMessage(selection, transcript);
            await DeliverGeneratedReplyAsync(message, "ask-model", session, transcript);
            return;
        }

        if (!string.IsNullOrEmpty(_selectedText)) {
            CopyTranscript(transcript);
            await DeliverGeneratedReplyAsync(
                SelectionCapture.BuildModelMessage(_selectedText, transcript),
                "selection-edit",
                session,
                transcript);
            return;
        }

        if (TryTriggerWorkspaceMode(transcript)) {
            return;
        }

        if (AutoCleanHere) {
            await DeliverCleanedAsync(transcript, session);
            return;
        }

        Log.Write($"dictation: delivering {transcript.Length} chars");
        DeliverPaste(transcript, "dictation");
    }

    /// <summary>
    /// Auto Clean: the transcription window only transcribed; the cleaning window (a different
    /// model when configured) rewrites it. If cleaning fails the raw transcript is pasted so
    /// nothing is lost.
    /// </summary>
    private async Task DeliverCleanedAsync(string transcript, int session) {
        if (!IsLive(session)) {
            return;
        }
        SetPhase(AppPhase.Generating);
        ShowProgress(WorkStage.Cleaning, Gen.Site.DisplayName);
        CopyTranscript(transcript);
        string message = CleanPromptHere.Trim() + "\n\n" + transcript;
        Log.Write("auto-clean: sending " + message.Length + " chars to " + Gen.Site.DisplayName);
        string? cleaned = null;
        try {
            // Hard cap so a stuck cleaning page can never block the paste: after this
            // long the raw transcript is pasted instead.
            Task<ModelReply> work = RequestCommandReplyAsync(message, session);
            Task finished = await Task.WhenAny(work, Task.Delay(AutoCleanMaxWait));
            if (finished != work) {
                Log.Write("auto-clean: no reply within " + (int)AutoCleanMaxWait.TotalSeconds + "s, giving up");
                try { Gen.Driver?.InvalidateWaits(); } catch { /* best effort */ }
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe late errors
            } else {
                ModelReply reply = await work;
                // Not an echo check: already-clean dictation legitimately comes back unchanged.
                if (reply.Text.Length > 0) {
                    cleaned = reply.Text;
                }
            }
        } catch (DriverException ex) {
            Log.Write("auto-clean: failed (" + ex.Kind + ": " + ex.Message + ")");
            if (ex.Kind == DriverFailure.LoggedOut && IsLive(session)) {
                Gen.ShowLoginWindow();
            }
        } catch (Exception ex) {
            Log.Write("auto-clean: unexpected " + ex.GetType().Name + ": " + ex.Message);
        }
        if (!IsLive(session)) {
            return;
        }
        if (cleaned == null) {
            Log.Write("auto-clean: no usable reply, pasting raw transcript");
            ShowBalloon(Loc.T("Auto Clean failed — pasted the raw transcript."), OverlayKind.Warning);
            DeliverPaste(transcript, "dictation");
            return;
        }
        // The cleaning model may turn dialect words back into standard spelling; re-apply the fixes.
        cleaned = ApplyWordReplacements(cleaned);
        Log.Write("auto-clean: delivering " + cleaned.Length + " chars");
        DeliverGeneratedPaste(cleaned, "auto-clean", transcript);
    }

    private async Task<CommandButton?> PickCommandButtonAsync(
        CustomCommand command,
        IReadOnlyList<CommandButton> buttons,
        int session) {
        if (!IsLive(session) || buttons.Count == 0) {
            return null;
        }

        SetPhase(AppPhase.ChoosingAction);
        _hotkey.EscSwallowActive = true;
        Log.Write("command: showing " + buttons.Count + " action buttons for " + command.DisplayName);

        var tcs = new TaskCompletionSource<CommandButton?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var form = new CommandButtonPickerForm(command.DisplayName, buttons, tcs);
        _actionPicker = form;
        form.PlaceNearCursor();
        form.Show();
        form.Activate();
        try {
            return await tcs.Task;
        } finally {
            if (ReferenceEquals(_actionPicker, form)) {
                _actionPicker = null;
            }
            if (!form.IsDisposed) {
                form.Dispose();
            }
            if (IsLive(session)) {
                _hotkey.EscSwallowActive = false;
            }
        }
    }

    private void DismissActionPicker() {
        var picker = _actionPicker;
        _actionPicker = null;
        picker?.Dismiss();
    }

    private async Task DeliverGeneratedReplyAsync(string message, string logLabel, int session, string transcript) {
        if (!IsLive(session)) {
            return;
        }
        SetPhase(AppPhase.Generating);
        ShowProgress(WorkStage.Thinking, ReplyWeb.Site.DisplayName);
        CopyTranscript(transcript);
        Log.Write(logLabel + ": sending " + message.Length + " chars");

        try {
            ModelReply reply = await RequestCommandReplyAsync(message, session);
            if (!IsLive(session)) {
                return;
            }
            if (_askModel) {
                await DeliverAskReplyAsync(reply, message, transcript, session);
                return;
            }
            if (reply.Images.Count > 0) {
                Log.Write(logLabel + ": reply has " + reply.Images.Count + " image(s)");
                byte[]? imageBytes = null;
                try {
                    if (ReplyWeb.Driver != null) {
                        imageBytes = await ReplyWeb.Driver.DownloadBestAssistantImageAsync(reply);
                    }
                } catch (Exception ex) {
                    Log.Write(logLabel + ": image download failed: " + ex.Message);
                }
                if (!IsLive(session)) {
                    return;
                }
                if (imageBytes is { Length: > 0 } && TryDeliverImage(imageBytes, logLabel)) {
                    return;
                }
                Log.Write(logLabel + ": falling back to text after image download failed");
            }

            if (reply.Text.Length == 0 || ReplyIsPromptEcho(reply.Text, message, transcript)) {
                if (reply.Text.Length > 0) {
                    Log.Write(logLabel + ": reply matched the prompt/transcript, ignoring it");
                } else {
                    Log.Write(logLabel + ": empty reply after retries");
                }
                Sounds.Error();
                CopyTranscript(transcript);
                ShowBalloon(
                    Loc.F("{0} returned an empty reply. Transcript copied to clipboard.", ReplyWeb.Site.DisplayName),
                    OverlayKind.Error);
                ResetToIdle();
                return;
            }

            Log.Write(logLabel + ": delivering " + reply.Text.Length + " chars");
            DeliverGeneratedPaste(reply.Text, logLabel, transcript);
        } catch (DriverException ex) {
            if (!IsLive(session)) {
                return;
            }
            try {
                if (ReplyWeb.Driver != null) {
                    await ReplyWeb.Driver.ClearComposerAsync();
                }
            } catch { /* best effort */ }
            if (IsLive(session)) {
                HandleFailure(ex, transcript);
            }
        } catch (Exception ex) when (ex is not DriverException) {
            // Anything unexpected (WebView2 COM errors, disposed page…) must not leave the
            // app stuck in "Generating" with nothing pasted.
            if (!IsLive(session)) {
                return;
            }
            Log.Write(logLabel + ": unexpected " + ex.GetType().Name + ": " + ex.Message);
            HandleFailure(new DriverException(DriverFailure.JavaScript, ex.Message), transcript);
        }
    }

    /// <summary>
    /// Ask model: the complete reply (the wait already covered generated images and videos)
    /// opens in a popup with Copy buttons for the text and the image. With the popup turned
    /// off it is pasted into the field you started in instead — the image if there is one,
    /// otherwise the text — or left on the clipboard when no field was focused.
    /// </summary>
    private async Task DeliverAskReplyAsync(ModelReply reply, string message, string transcript, int session) {
        const string label = "ask-model";
        var web = ReplyWeb;
        string modelName = web.Site.DisplayName;
        string text = AskReplyForm.CleanText(reply.Text);
        if (text.Length > 0 && ReplyIsPromptEcho(text, message, transcript)) {
            Log.Write(label + ": reply matched the prompt/transcript, ignoring its text");
            text = "";
        }

        Image? image = null;
        if (reply.Images.Count > 0) {
            Log.Write(label + ": reply has " + reply.Images.Count + " image(s)");
            try {
                if (web.Driver != null) {
                    byte[]? bytes = await web.Driver.DownloadBestAssistantImageAsync(reply);
                    image = bytes is { Length: > 0 } ? DecodeImage(bytes, label) : null;
                }
            } catch (Exception ex) {
                Log.Write(label + ": image download failed: " + ex.Message);
            }
            if (!IsLive(session)) {
                image?.Dispose();
                return;
            }
        }
        if (reply.Videos.Count > 0) {
            Log.Write(label + ": reply has " + reply.Videos.Count + " video(s)");
        }

        string? videoUrl = reply.Videos.FirstOrDefault(v => v.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        string pasteText = text.Length > 0 ? text : videoUrl ?? "";
        if (image == null && pasteText.Length == 0 && reply.Videos.Count == 0) {
            Log.Write(label + ": empty reply after retries");
            Sounds.Error();
            CopyTranscript(transcript);
            ShowBalloon(Loc.F("{0} returned an empty reply. Transcript copied to clipboard.", modelName), OverlayKind.Error);
            ResetToIdle();
            return;
        }

        if (_settings.ShowAskReplyWindow) {
            // Popup only: nothing is pasted into the field; copy from the popup's buttons.
            Log.Write(label + ": shown in popup (text " + text.Length + " chars, image " + (image != null) + ")");
            Sounds.Pasted();
            AskReplyForm.ShowReply(
                new AskReplyContent(modelName, transcript, text, image, reply.Images.Count, reply.Videos,
                    Loc.T("Use the Copy buttons to copy the reply.")),
                () => {
                    web.ShowLoginWindow();
                    NotifyUi();
                });
            ResetToIdle();
            return;
        }

        // Paste: the image when there is one (that is what was asked for), else the text.
        Paster.Outcome? outcome = null;
        if (image != null) {
            var result = Paster.DeliverImage(image, PressEnterHere, _pasteTarget);
            if (result.Outcome is Paster.Outcome.Pasted or Paster.Outcome.CopiedToClipboard) {
                outcome = result.Outcome;
            }
        }
        if (outcome == null && pasteText.Length > 0) {
            outcome = Paster.Deliver(pasteText, keepTranscriptOnClipboard: true, PressEnterHere, _pasteTarget).Outcome;
        }
        Log.Write(label + ": delivered (" + (outcome?.ToString() ?? "popup only") + ", text "
            + text.Length + " chars, image " + (image != null) + ")");
        Sounds.Pasted();

        string note = outcome switch {
            Paster.Outcome.Pasted => Loc.T("Pasted into the field you started in. It is also on the clipboard."),
            Paster.Outcome.CopiedToClipboard => Loc.T("No text field was focused — the reply is on the clipboard (Ctrl+V)."),
            _ => Loc.T("Nothing to paste — open the video from the model window."),
        };
        image?.Dispose();
        ShowBalloon(outcome == Paster.Outcome.Pasted ? Loc.T("Pasted the reply.") : note, OverlayKind.Success);
        ResetToIdle();
    }

    private static Image? DecodeImage(byte[] bytes, string logLabel) {
        try {
            using var ms = new MemoryStream(bytes);
            using var temp = Image.FromStream(ms);
            return new Bitmap(temp);
        } catch (Exception ex) {
            Log.Write(logLabel + ": image decode failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Translate shortcut: the model page only transcribed; the translation runs over plain
    /// HTTP, so there is no wait for a model reply. With an OpenAI API key it goes through
    /// OpenAI (handles mixed Arabic/English text); otherwise, or if that call fails, through
    /// Google Translate (GTranslate). On failure the original transcript is left on the clipboard.
    /// </summary>
    private async Task DeliverTranslationAsync(string transcript, int session) {
        if (!IsLive(session)) {
            return;
        }
        SetPhase(AppPhase.Generating);
        ShowProgress(WorkStage.Translating,
            OpenAiTranslation.ResolveKey(_settings).Length > 0 ? "OpenAI" : "Google Translate");
        CopyTranscript(transcript);
        string target = TranslateTargetHere;

        string translated;
        try {
            translated = await TranslateAsync(transcript, target);
        } catch (Exception ex) {
            if (!IsLive(session)) {
                return;
            }
            Log.Write("translate: failed: " + ex.Message);
            Sounds.Error();
            ShowBalloon(Loc.T("Translation failed — transcript copied to clipboard."), OverlayKind.Error);
            ResetToIdle();
            return;
        }
        if (!IsLive(session)) {
            return;
        }
        if (translated.Length == 0) {
            Log.Write("translate: empty translation");
            Sounds.Error();
            ShowBalloon(Loc.T("The translation came back empty. Transcript copied to clipboard."), OverlayKind.Error);
            ResetToIdle();
            return;
        }

        Log.Write("translate: delivering " + translated.Length + " chars");
        Paster.DeliverResult result = Paster.Deliver(
            translated, keepTranscriptOnClipboard: true, PressEnterHere, _pasteTarget);
        switch (result.Outcome) {
            case Paster.Outcome.Pasted:
                // The original transcript was copied first; the translation stays last on the
                // clipboard so a later Ctrl+V pastes the translation, not the original.
                Sounds.Pasted();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write("translate: no editable field focused, left translation on clipboard");
                Sounds.Pasted();
                ShowBalloon(Loc.T("Copied the translation — press Ctrl+V to paste."), OverlayKind.Success);
                break;
        }
        ResetToIdle();
    }

    private async Task<string> TranslateAsync(string transcript, string target) {
        string apiKey = OpenAiTranslation.ResolveKey(_settings);
        if (apiKey.Length > 0) {
            Log.Write("translate: sending " + transcript.Length + " chars to OpenAI (to " + target + ")");
            var started = DateTime.UtcNow;
            try {
                string result = (await OpenAiTranslation.TranslateAsync(
                    transcript, target, apiKey, _settings.OpenAiTranslationModel)).Trim();
                Log.Write("translate: OpenAI replied in " + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms");
                if (result.Length > 0) {
                    return result;
                }
                Log.Write("translate: OpenAI returned nothing, falling back to Google Translate");
            } catch (Exception ex) {
                Log.Write("translate: OpenAI failed (" + ex.Message + "), falling back to Google Translate");
            }
        }
        Log.Write("translate: sending " + transcript.Length + " chars to Google Translate (to " + target + ")");
        return (await GoogleTranslation.TranslateAsync(transcript, target)).Trim();
    }

    // ------------------------------------------------------------------
    // Translate selection (popup)
    // ------------------------------------------------------------------

    private bool IsTranslateSelectionChord(HotkeyChord chord) =>
        !_settings.TranslateSelectionChord.IsEmpty && chord.Equals(_settings.TranslateSelectionChord);

    /// <summary>
    /// Translate selection shortcut: copies the text selected in the foreground app,
    /// translates it to the Translate language and shows it in a popup with its layout
    /// kept — lines, blank lines, indentation, bullets and spacing as in the original.
    /// Nothing is pasted and the clipboard is restored. Runs beside the dictation state
    /// machine, so it only starts while EchoType is idle.
    /// </summary>
    private async Task TranslateSelectionAsync() {
        if (_selectionTranslateCts != null) {
            Log.Write("translate-selection: already running");
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon(Loc.T("Wait until dictation finishes before translating."), OverlayKind.Warning);
            return;
        }
        using var cts = new CancellationTokenSource();
        _selectionTranslateCts = cts;
        try {
            // Copy once the shortcut's modifiers are up: with Shift still held the app would
            // get Ctrl+Shift+C (DevTools in browsers) instead of Ctrl+C.
            var wait = Stopwatch.StartNew();
            while (NativeMethods.AnyModifierDown() && wait.ElapsedMilliseconds < 1500) {
                await Task.Delay(15);
            }
            string? selected = await SelectionCapture.CaptureAsync();
            if (cts.IsCancellationRequested) {
                return;
            }
            if (string.IsNullOrWhiteSpace(selected)) {
                Log.Write("translate-selection: no selection");
                ShowBalloon(Loc.F("Select some text first, then press {0}.",
                    HotkeyNames.For(_settings.TranslateSelectionChord)), OverlayKind.Warning);
                return;
            }

            string target = _settings.TranslateSelectionTargetLanguage;
            string engine = OpenAiTranslation.ResolveKey(_settings).Length > 0 ? "OpenAI" : "Google Translate";
            Log.Write("translate-selection: " + selected.Length + " chars to " + target);
            StatusOverlay.ShowWorking(WorkStage.Translating, engine, cap: 0.94f, restart: true);
            string translated = await LayoutPreservingTranslation.TranslateAsync(
                selected, pieces => TranslatePiecesAsync(pieces, target));
            if (cts.IsCancellationRequested) {
                return;
            }
            // Dictation may have started meanwhile; its HUD owns the overlay then.
            if (_phase == AppPhase.Idle) {
                StatusOverlay.CompleteWorking();
            }
            Log.Write("translate-selection: showing " + translated.Length + " chars");
            Sounds.Pasted();
            AskReplyForm.ShowReply(
                new AskReplyContent(engine, selected, translated, null, 0, [],
                    Loc.T("Use Copy text to copy the translation."),
                    Title: Loc.F("Translation → {0}", GoogleTranslation.NameFor(target)),
                    KeepLayout: true),
                openModelWindow: null);
        } catch (Exception ex) {
            if (cts.IsCancellationRequested) {
                return;
            }
            Log.Write("translate-selection: failed: " + ex.Message);
            if (_phase == AppPhase.Idle) {
                StatusOverlay.HideWorking();
            }
            Sounds.Error();
            ShowBalloon(Loc.T("Translation failed — check your internet connection and try again."), OverlayKind.Error);
        } finally {
            _selectionTranslateCts = null;
        }
    }

    /// <summary>OpenAI when a key is set (falls back to Google Translate on failure), else Google Translate.</summary>
    private async Task<IReadOnlyList<string>> TranslatePiecesAsync(IReadOnlyList<string> pieces, string target) {
        string apiKey = OpenAiTranslation.ResolveKey(_settings);
        if (apiKey.Length > 0) {
            Log.Write("translate-selection: sending " + pieces.Count + " piece(s) to OpenAI");
            try {
                return await OpenAiTranslation.TranslatePiecesAsync(
                    pieces, target, apiKey, _settings.OpenAiTranslationModel);
            } catch (Exception ex) {
                Log.Write("translate-selection: OpenAI failed (" + ex.Message + "), falling back to Google Translate");
            }
        }
        Log.Write("translate-selection: sending " + pieces.Count + " piece(s) to Google Translate");
        return await GoogleTranslation.TranslatePiecesAsync(pieces, target);
    }

    /// <summary>
    /// The default Translate selection shortcut (Ctrl+Shift+Y) reaches older configs too;
    /// drop it if another shortcut already uses the same keys.
    /// </summary>
    private void DropTranslateSelectionChordIfTaken() {
        var chord = _settings.TranslateSelectionChord;
        if (chord.IsEmpty || CollectHotkeys().Count(c => c.Equals(chord)) <= 1) {
            return;
        }
        Log.Write("settings: translate-selection " + HotkeyNames.For(chord) + " already in use, cleared");
        _settings.SetTranslateSelectionChord(default);
        _settings.Save();
        UpdateTranslateMenu();
    }

    private void DeliverPaste(string text, string logLabel) {
        Paster.DeliverResult result = Paster.Deliver(
            text, _settings.KeepTranscriptOnClipboard, PressEnterHere, _pasteTarget);
        switch (result.Outcome) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                if (result.KeptUserFocus) {
                    ShowBalloon(Loc.T("Pasted into the original field."), OverlayKind.Success);
                }
                ResetToIdle();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write(logLabel + ": no editable field focused, left on clipboard");
                Sounds.Pasted();
                ShowBalloon(Loc.T("Copied to clipboard — press Ctrl+V to paste."), OverlayKind.Success);
                ResetToIdle();
                break;
        }
    }

    /// <summary>
    /// Pastes the model reply only. The spoken transcript was already copied when
    /// it landed; after a successful paste it is put back on the clipboard once
    /// the target app has had time to read the reply (Qt/Electron paste late).
    /// If there was nowhere to paste, the reply stays on the clipboard.
    /// </summary>
    private void DeliverGeneratedPaste(string reply, string logLabel, string transcript) {
        Paster.DeliverResult result = Paster.Deliver(
            reply, keepTranscriptOnClipboard: true, PressEnterHere, _pasteTarget);
        switch (result.Outcome) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                Paster.CopyLater(transcript);
                ShowBalloon(Loc.T("Pasted the reply. Transcript copied to clipboard."), OverlayKind.Success);
                ResetToIdle();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write(logLabel + ": no editable field focused, left reply on clipboard");
                Sounds.Pasted();
                ShowBalloon(Loc.T("Copied the reply — press Ctrl+V to paste."), OverlayKind.Success);
                ResetToIdle();
                break;
        }
    }

    /// <summary>
    /// True when the scraped "reply" is actually the prompt or spoken transcript
    /// (a user bubble mistaken for the assistant).
    /// </summary>
    private static bool ReplyIsPromptEcho(string reply, string message, string transcript) {
        string text = reply.Trim();
        if (text.Length == 0) {
            return true;
        }
        if (string.Equals(text, transcript.Trim(), StringComparison.Ordinal)) {
            return true;
        }
        return string.Equals(text, message.Trim(), StringComparison.Ordinal);
    }

    private bool TryDeliverImage(byte[] imageBytes, string logLabel) {
        Image image;
        try {
            using var ms = new MemoryStream(imageBytes);
            using var temp = Image.FromStream(ms);
            image = new Bitmap(temp);
        } catch (Exception ex) {
            Log.Write(logLabel + ": image decode failed: " + ex.Message);
            return false;
        }

        using (image) {
            Paster.DeliverResult result = Paster.DeliverImage(image, PressEnterHere, _pasteTarget);
            switch (result.Outcome) {
                case Paster.Outcome.Pasted:
                    Sounds.Pasted();
                    ShowBalloon(Loc.T("Pasted image. Transcript copied to clipboard."), OverlayKind.Success);
                    ResetToIdle();
                    return true;
                case Paster.Outcome.CopiedToClipboard:
                    Log.Write(logLabel + ": no editable field focused, left image on clipboard");
                    Sounds.Pasted();
                    ShowBalloon(Loc.T("Copied image. Transcript copied to clipboard."), OverlayKind.Success);
                    ResetToIdle();
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Sends the custom-command prompt and waits for a reply. ChatGPT reuses the
    /// current web thread for up to 25 successful sends; a timeout, empty reply,
    /// or that cap opens a new chat. Gemini still starts a fresh chat every time.
    /// If ChatGPT never starts a reply, the same prompt + transcript are resent
    /// in a new chat, then the page is refreshed as a last fallback.
    /// </summary>
    private async Task<ModelReply> RequestCommandReplyAsync(string message, int session) {
        if (!ReferenceEquals(ReplyWeb, _web)) {
            // The cleaning window is never touched by dictation, so make sure its page is
            // loaded and signed in (it may have been reloaded or navigated since warmup).
            var ready = await ReplyWeb.EnsureReadyAsync();
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            if (ready != ReadyOutcome.Ready) {
                throw ToDriverException(ready);
            }
        }
        TimeSpan firstByteTimeout = CommandFirstByteTimeout();
        Log.Write("command: waiting up to " + ((int)firstByteTimeout.TotalSeconds)
            + "s for a reply (recording " + HeldSeconds().ToString("0.0") + "s)");
        // Image questions get a fresh chat: a reused ChatGPT thread could still hold
        // attachments from an earlier failed send.
        bool firstNeedsNewChat = (_askModel && _askImages.Count > 0) || ShouldStartNewChat();
        try {
            ModelReply reply = await SendCommandOnceAsync(message, firstNeedsNewChat, firstByteTimeout);
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            if (!reply.IsEmpty) {
                NoteChatGptThreadSuccess(openedNewChat: firstNeedsNewChat);
                return reply;
            }
            Log.Write("command: empty reply");
            MarkChatGptThreadFailed();
        } catch (DriverException ex) when (IsCommandRetryable(ex)) {
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            Log.Write("command: first send failed (" + ex.Kind + ": " + ex.Message + ")");
            MarkChatGptThreadFailed();
        }

        if (!IsLive(session)) {
            return ModelReply.Empty;
        }
        Log.Write("command: retrying in a new chat");
        ShowBalloon(Loc.F("{0} is slow — retrying in a new chat…", ReplyWeb.Site.DisplayName), OverlayKind.Warning);
        try {
            ModelReply reply = await SendCommandOnceAsync(message, startNewChat: true, firstByteTimeout);
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            if (!reply.IsEmpty) {
                NoteChatGptThreadSuccess(openedNewChat: true);
                return reply;
            }
            Log.Write("command: empty reply after new-chat retry");
            MarkChatGptThreadFailed();
        } catch (DriverException ex) when (IsCommandRetryable(ex)) {
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            Log.Write("command: new-chat retry failed (" + ex.Kind + ": " + ex.Message + ")");
            MarkChatGptThreadFailed();
        }

        if (!IsLive(session)) {
            return ModelReply.Empty;
        }
        Log.Write("command: refreshing " + ReplyWeb.Site.DisplayName + " and retrying");
        ShowBalloon(Loc.F("Still waiting — refreshing {0} and retrying…", ReplyWeb.Site.DisplayName), OverlayKind.Warning);
        var outcome = await ReplyWeb.RefreshChatAsync();
        if (!IsLive(session)) {
            return ModelReply.Empty;
        }
        if (outcome != ReadyOutcome.Ready) {
            throw ToDriverException(outcome);
        }
        ModelReply refreshed = await SendCommandOnceAsync(message, startNewChat: true, firstByteTimeout);
        if (!refreshed.IsEmpty) {
            NoteChatGptThreadSuccess(openedNewChat: true);
        } else {
            MarkChatGptThreadFailed();
        }
        return refreshed;
    }

    private async Task<ModelReply> SendCommandOnceAsync(
        string message,
        bool startNewChat,
        TimeSpan firstByteTimeout) {
        var driver = ReplyWeb.Driver
            ?? throw new DriverException(DriverFailure.NotReady, "no driver");
        driver.InvalidateWaits();
        if (startNewChat) {
            if (!await driver.StartNewChatAsync()) {
                throw new DriverException(DriverFailure.ButtonNotFound, "couldn't open a new chat");
            }
        } else {
            await driver.StopGenerationAsync();
        }
        CommandTurnSnapshot previous = await driver.SnapshotCommandTurnAsync();
        await driver.SetComposerAsync(message);
        if (_askModel && _askImages.Count > 0 && !await driver.AttachImagesAsync(_askImages)) {
            ShowBalloon(Loc.T("Couldn't confirm the image was attached — sending the question anyway."), OverlayKind.Warning);
        }
        await driver.SendPromptAsync();
        ModelReply reply;
        try {
            // Ask model waits for the complete reply (generated images and videos included).
            reply = await driver.AwaitAssistantReplyAsync(previous, firstByteTimeout, waitForMedia: _askModel);
        } catch (DriverException ex) when (ex.Kind == DriverFailure.Timeout) {
            reply = await driver.TryRecoverAssistantReplyAsync(previous);
            if (reply.IsEmpty) {
                throw;
            }
            Log.Write("command: recovered the model's reply after a wait timeout");
        }
        if (reply.IsEmpty) {
            reply = await driver.TryRecoverAssistantReplyAsync(previous);
        }
        await driver.ClearComposerAsync();
        return reply;
    }

    /// <summary>
    /// How long to wait for the first reply token before treating ChatGPT as hung.
    /// Matches recording length, clamped to 30–60 seconds so a 30s clip retries
    /// after 30s and a two-minute clip waits a full minute.
    /// </summary>
    private TimeSpan CommandFirstByteTimeout() {
        if (!IsChatGptSite) {
            return TimeSpan.FromSeconds(45);
        }
        double seconds = Math.Clamp(HeldSeconds(), MinChatGptFirstReplySeconds, MaxChatGptFirstReplySeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private bool ShouldStartNewChat() {
        if (!IsChatGptSite) {
            return true;
        }
        var web = ReplyWeb;
        if (web.ForceNewChatThread) {
            Log.Write("command: ChatGPT thread needs a new chat after an error");
            return true;
        }
        if (web.ChatThreadSends >= ChatGptThreadReuseLimit) {
            Log.Write("command: ChatGPT thread reached " + ChatGptThreadReuseLimit + " sends, opening a new chat");
            return true;
        }
        Log.Write("command: reusing ChatGPT thread (" + (web.ChatThreadSends + 1) + "/" + ChatGptThreadReuseLimit + ")");
        return false;
    }

    private void NoteChatGptThreadSuccess(bool openedNewChat) {
        if (!IsChatGptSite) {
            return;
        }
        var web = ReplyWeb;
        web.ForceNewChatThread = false;
        web.ChatThreadSends = openedNewChat ? 1 : web.ChatThreadSends + 1;
    }

    private void MarkChatGptThreadFailed() {
        if (!IsChatGptSite) {
            return;
        }
        ReplyWeb.ForceNewChatThread = true;
    }

    /// <summary>Cleaning-model window: Auto Clean, custom commands and selection rewrites.</summary>
    private ChatGPTWebController Gen => _cleanWeb ?? _web;

    /// <summary>Ask-model window: its own, or the transcription/cleaning window when the model is the same.</summary>
    private ChatGPTWebController AskWeb =>
        _askWeb ?? (_settings.AskProvider == _settings.TranscriptionProvider ? _web : Gen);

    /// <summary>
    /// Window that answers the current session. Ask model uses the Ask model;
    /// everything else goes to the cleaning model.
    /// </summary>
    private ChatGPTWebController ReplyWeb => _askModel ? AskWeb : Gen;

    private bool IsChatGptSite => ReplyWeb.Site.Id == ChatSite.ChatGpt.Id;

    private static bool IsCommandRetryable(DriverException ex) =>
        ex.Kind is DriverFailure.Timeout or DriverFailure.ButtonNotFound
            or DriverFailure.JavaScript or DriverFailure.NotReady;

    private DriverException ToDriverException(ReadyOutcome outcome) => outcome switch {
        ReadyOutcome.LoggedOut => new DriverException(DriverFailure.LoggedOut, "logged out"),
        ReadyOutcome.Offline => new DriverException(DriverFailure.Offline, "offline"),
        ReadyOutcome.Timeout => new DriverException(DriverFailure.Timeout, _web.Site.DisplayName + " didn't respond in time"),
        ReadyOutcome.UiChanged => new DriverException(DriverFailure.UiChanged, SelectorHealth.ComposerPart),
        _ => new DriverException(DriverFailure.NotReady, outcome.ToString()),
    };

    /// <summary>"ChatGPT's page changed" — shown instead of a generic start failure.</summary>
    private static string UiChangedMessage(string siteName, string missingPart) =>
        missingPart == SelectorHealth.MicPart
            ? Loc.F("{0}'s page changed — EchoType can't find its mic button. Details are in the log.", siteName)
            : Loc.F("{0}'s page changed — EchoType can't find its message box. Details are in the log.", siteName);

    private Task CancelAndResetAsync(string reason) {
        if (_phase == AppPhase.Idle) {
            return Task.CompletedTask;
        }
        Log.Write("dictation: " + reason);
        StopListenWatch();
        _session++;
        _hotkey.EscSwallowActive = false;
        DismissActionPicker();
        bool wasGenerating = _phase == AppPhase.Generating;
        try {
            _web.Driver?.InvalidateWaits();
            _cleanWeb?.Driver?.InvalidateWaits();
            _askWeb?.Driver?.InvalidateWaits();
        } catch { /* best effort */ }
        _engagementFailures = 0;
        // Cancel leaves ChatGPT/Gemini's mic UI wedged often enough that the next
        // recording does not start until a later self-heal reload. Reload now so
        // the next hotkey waits on a fresh page instead of clicking a stuck mic.
        (wasGenerating ? ReplyWeb : _web).ReloadAfterCancel();
        StatusOverlay.HideWorking();
        ResetToIdle();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Failure handling (mac handleFailure parity)
    // ------------------------------------------------------------------

    private void HandleFailure(DriverException ex, string? transcriptToCopy = null) {
        var web = _phase == AppPhase.Generating ? ReplyWeb : _web;
        string message;
        switch (ex.Kind) {
            case DriverFailure.LoggedOut:
                message = Loc.T("Logged out — open the EchoType menu to log in");
                if (ReferenceEquals(web, _web)) {
                    _loggedIn = false;
                    UpdateStatusIcon();
                    UpdateLoginMenuItem();
                }
                web.ShowLoginWindow();
                break;
            case DriverFailure.Offline:
                message = Loc.T("No internet connection — check your network");
                _online = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                break;
            case DriverFailure.Timeout:
                message = Loc.F("{0} didn't respond in time", web.Site.DisplayName);
                break;
            case DriverFailure.ButtonNotFound:
                // The page wedges occasionally; only reload after two failures in a row.
                _engagementFailures++;
                if (_engagementFailures >= 2) {
                    message = Loc.F("Dictation glitched ({0}) — reloading {1}, try again", ex.Message, web.Site.DisplayName);
                    Log.Write($"dictation: {_engagementFailures} engagement failures, reloading webview to self-heal");
                    _engagementFailures = 0;
                    _ = web.ReloadInBackgroundAsync();
                } else {
                    message = ex.Message.Contains("send", StringComparison.OrdinalIgnoreCase)
                        ? Loc.T("Couldn't send the prompt — try again")
                        : ex.Message.Contains("new chat", StringComparison.OrdinalIgnoreCase)
                            ? Loc.T("Couldn't start a new chat — try again")
                            : Loc.T("Dictation didn't start — try again");
                }
                break;
            case DriverFailure.UiChanged:
                message = UiChangedMessage(web.Site.DisplayName, ex.Message);
                // Retrying the same selectors won't help, but a fresh load re-reads
                // selectors.json, so a patched selector takes effect on the next try.
                _engagementFailures = 0;
                _ = web.ReloadInBackgroundAsync();
                break;
            case DriverFailure.NotReady:
                message = Loc.F("{0} isn't ready yet", web.Site.DisplayName);
                break;
            default:
                message = Loc.T("Page error: ") + Truncate(ex.Message, 60);
                break;
        }

        Log.Write("dictation: failed — " + message);
        Sounds.Error();
        if (!string.IsNullOrEmpty(transcriptToCopy) && CopyTranscript(transcriptToCopy)) {
            message += Loc.T(" Transcript copied to clipboard.");
        }
        ShowBalloon(message, OverlayKind.Error);
        ResetToIdle();
    }

    /// <summary>
    /// Saves the spoken transcript so a model timeout or empty reply does not
    /// throw away the dictation. Returns false if the clipboard write failed.
    /// </summary>
    private static bool CopyTranscript(string transcript) {
        if (string.IsNullOrWhiteSpace(transcript)) {
            return false;
        }
        try {
            Paster.Copy(transcript);
            Log.Write("clipboard: copied transcript (" + transcript.Length + " chars)");
            return true;
        } catch (Exception ex) {
            Log.Write("clipboard: transcript copy failed: " + ex.Message);
            return false;
        }
    }

    // Global settings with the current session's app profile applied on top.
    private bool PressEnterHere => _profile?.PressEnter ?? _settings.PressEnterAfterPaste;

    private string CleanPromptHere =>
        _profile is { CleanPrompt.Length: > 0 } ? _profile.CleanPrompt : _settings.AutoCleanPrompt;

    private bool AutoCleanHere =>
        (_profile?.AutoClean ?? _settings.AutoClean) && !string.IsNullOrWhiteSpace(CleanPromptHere);

    private string TranslateTargetHere =>
        _profile is { TranslateTo.Length: > 0 } ? _profile.TranslateTo : _settings.TranslateTargetLanguage;

    private void ResetToIdle() {
        StopListenWatch();
        DismissActionPicker();
        RestoreOtherApps();
        _hotkeyHeld = false;
        _activeCommand = null;
        _askModel = false;
        _askImages = [];
        _translate = false;
        _sessionChord = default;
        _pasteTarget = null;
        _profile = null;
        _selectedText = null;
        _selectionTask = Task.FromResult<string?>(null);
        _longPieces.Clear();
        _longContinuing = false;
        _longEmptyPieces = 0;
        _pieceStartedAt = default;
        _hotkey.EscSwallowActive = false;
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        SetPhase(AppPhase.Idle);
        _listenFinishGate = 0;
        _session++;
    }

    private void StartListenWatch(int session) {
        StopListenWatch();
        var cts = new CancellationTokenSource();
        _listenWatchCts = cts;
        _ = WatchListeningCutoffAsync(session, cts.Token);
    }

    private void StopListenWatch() {
        CancellationTokenSource? cts = _listenWatchCts;
        _listenWatchCts = null;
        if (cts == null) {
            return;
        }
        try {
            cts.Cancel();
        } catch (ObjectDisposedException) {
            return;
        }
        cts.Dispose();
    }

    private void StartMicLevelPump() {
        StopMicLevelPump();
        var cts = new CancellationTokenSource();
        _micLevelCts = cts;
        _ = PumpWebMicLevelAsync(cts.Token);
        // Every model gets the live pump: any site that writes words into its composer
        // while you speak (Claude does) shows them under the wave; a site that only fills
        // the composer after you stop (ChatGPT) leaves the pill compact.
        _ = PumpLiveTranscriptAsync(cts.Token);
    }

    private void StopMicLevelPump() {
        CancellationTokenSource? cts = _micLevelCts;
        _micLevelCts = null;
        if (cts == null) {
            return;
        }
        try {
            cts.Cancel();
        } catch (ObjectDisposedException) {
            return;
        }
        cts.Dispose();
    }

    /// <summary>
    /// Reads the page analyser ~25×/s so the HUD wave tracks the same audio
    /// ChatGPT/Gemini is capturing. WASAPI remains as a fallback inside the meter.
    /// </summary>
    private async Task PumpWebMicLevelAsync(CancellationToken cancel) {
        while (!cancel.IsCancellationRequested) {
            try {
                var driver = _web.Driver;
                if (driver != null) {
                    _micMeter.SetWebLevel(await driver.MicLevelAsync());
                }
            } catch (Exception) {
                // Keep the last sample; idle motion still runs on the overlay.
            }
            try {
                await Task.Delay(40, cancel);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    /// <summary>
    /// Live models write words into their composer while you speak. Mirror them in the
    /// Listening HUD so the transcript is visible live; the final text is still
    /// taken from the composer after recording stops.
    /// </summary>
    private async Task PumpLiveTranscriptAsync(CancellationToken cancel) {
        string shown = "";
        while (!cancel.IsCancellationRequested) {
            try {
                var driver = _web.Driver;
                // While Long dictation collects a piece the composer holds text that is about
                // to join the earlier pieces; keep showing what's already there.
                if (driver != null && !_longContinuing) {
                    string text = JoinLongPieces(await driver.LiveTranscriptAsync());
                    if (!cancel.IsCancellationRequested && !_longContinuing && text != shown) {
                        shown = text;
                        StatusOverlay.SetLiveTranscript(text);
                    }
                }
            } catch (Exception) {
                // Keep the last text; the final transcript is read separately.
            }
            try {
                await Task.Delay(200, cancel);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task WatchListeningCutoffAsync(int session, CancellationToken cancel) {
        var driver = _web.Driver;
        if (driver == null) {
            return;
        }
        string? reason;
        try {
            reason = await driver.AwaitUnexpectedStopAsync(cancel);
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            Log.Write("dictation: listen watch failed: " + ex.Message);
            return;
        }
        if (reason == null || cancel.IsCancellationRequested || !IsLive(session) || _phase != AppPhase.Listening) {
            return;
        }

        if (_settings.LongDictation && _hotkeyHeld) {
            await ContinueLongDictationAsync(session, reason);
            return;
        }

        Log.Write("dictation: recording cut off (" + reason + ")");
        Sounds.RecordingStopped();
        ShowBalloon(
            Loc.F("{0} ended the audio. Only what you already said will be transcribed.", _web.Site.DisplayName),
            OverlayKind.Warning,
            Loc.T("Recording stopped"));
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        await FinishListeningAsync(session);
    }

    /// <summary>
    /// Long dictation: the site ended the recording while you're still talking. Keep the piece
    /// it captured, restart the page's dictation, and let <see cref="FinishListeningAsync"/>
    /// join every piece. A release (or toggle stop) meanwhile finishes with what was collected.
    /// </summary>
    private async Task ContinueLongDictationAsync(int session, string reason) {
        var driver = _web.Driver;
        if (driver == null) {
            return;
        }
        Log.Write($"dictation: recording cut off ({reason}) — long dictation keeps piece {_longPieces.Count + 1} ({PieceSeconds():F1}s)");
        _longContinuing = true;
        StopListenWatch();

        string piece;
        try {
            await driver.SettleCutoffAsync();
            if (!IsLive(session)) {
                return;
            }
            piece = (await driver.AwaitTranscriptAsync(TimeSpan.FromSeconds(PieceSeconds()))).Trim();
            if (!IsLive(session)) {
                return;
            }
            await driver.ClearComposerAsync();
        } catch (DriverException ex) {
            if (!IsLive(session)) {
                return;
            }
            _longContinuing = false;
            try { await driver.ClearComposerAsync(); } catch { /* best effort */ }
            if (!IsLive(session)) {
                return;
            }
            if (_longPieces.Count == 0) {
                HandleFailure(ex);
                return;
            }
            Log.Write("dictation: long dictation piece failed (" + ex.Message + "), delivering earlier pieces");
            await FinishLongDictationEarlyAsync(session);
            return;
        }
        if (!IsLive(session)) {
            return;
        }

        if (piece.Length > 0) {
            _longPieces.Add(piece);
            _longEmptyPieces = 0;
            StatusOverlay.SetLiveTranscript(JoinLongPieces(""));
        } else {
            _longEmptyPieces++;
        }
        Log.Write($"dictation: long dictation piece {(piece.Length > 0 ? _longPieces.Count : 0)} = {piece.Length} chars");

        if (!_hotkeyHeld) {
            _longContinuing = false;
            await FinishListeningAsync(session, micOpen: false);
            return;
        }
        if (_longEmptyPieces >= 3) {
            // Nothing captured three times running: the page keeps stopping straight away.
            Log.Write("dictation: long dictation gave up after 3 empty pieces");
            _longContinuing = false;
            await FinishLongDictationEarlyAsync(session);
            return;
        }

        try {
            await driver.StartDictationAsync();
        } catch (DriverException ex) {
            if (!IsLive(session)) {
                return;
            }
            _longContinuing = false;
            if (_longPieces.Count == 0) {
                HandleFailure(ex);
                return;
            }
            Log.Write("dictation: long dictation restart failed (" + ex.Message + "), delivering what was said");
            await FinishLongDictationEarlyAsync(session);
            return;
        }
        if (!IsLive(session)) {
            return;
        }
        _longContinuing = false;
        _pieceStartedAt = DateTime.UtcNow;
        EnsureMicAtFullVolume();

        if (!_hotkeyHeld) {
            // Released while the page restarted: that new piece is a fraction of a second, and
            // waiting it out would hold the paste for ~30 s. Drop it and paste the rest.
            Log.Write("dictation: released while long dictation restarted — dropping the new piece");
            try { await driver.CancelDictationAsync(); } catch { /* best effort */ }
            try { await driver.ClearComposerAsync(); } catch { /* best effort */ }
            await FinishListeningAsync(session, micOpen: false);
            return;
        }
        Log.Write("dictation: long dictation restarted");
        Sounds.Start();
        StartListenWatch(session);
    }

    /// <summary>Long dictation couldn't go on while the key is still held: say so and paste what was collected.</summary>
    private async Task FinishLongDictationEarlyAsync(int session) {
        Sounds.RecordingStopped();
        ShowBalloon(
            Loc.F("{0} ended the audio. Only what you already said will be transcribed.", _web.Site.DisplayName),
            OverlayKind.Warning,
            Loc.T("Recording stopped"));
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        await FinishListeningAsync(session, micOpen: false);
    }

    /// <summary>The pieces Long dictation already collected, followed by <paramref name="current"/>.</summary>
    private string JoinLongPieces(string current) {
        if (_longPieces.Count == 0) {
            return current;
        }
        return string.Join(" ", _longPieces.Append(current.Trim()).Where(p => p.Length > 0));
    }

    /// <summary>
    /// Seconds in the current recording piece: the whole take until Long dictation restarts it,
    /// then the time since the latest restart.
    /// </summary>
    private double PieceSeconds() {
        if (_pieceStartedAt == default) {
            return HeldSeconds();
        }
        DateTime end = _releasedAt > _pieceStartedAt ? _releasedAt : DateTime.UtcNow;
        return Math.Max(0, (end - _pieceStartedAt).TotalSeconds);
    }

    /// <summary>
    /// Capture clients sometimes write a lower endpoint volume just after the
    /// stream opens. One later pass catches that without watching the whole take.
    /// </summary>
    private async Task RecheckMicVolumeSoonAsync(int session) {
        try {
            await Task.Delay(500);
        } catch (Exception) {
            return;
        }
        if (!IsLive(session) || _phase != AppPhase.Listening) {
            return;
        }
        EnsureMicAtFullVolume();
    }

    private static void EnsureMicAtFullVolume() {
        try {
            MicInputVolume.EnsureFull();
        } catch (Exception ex) {
            Log.Write("audio: mic volume check threw: " + ex.Message);
        }
    }

    private void MuteOtherAppsIfEnabled() {
        if (!_settings.MuteOtherAppsWhileDictating) {
            return;
        }
        try {
            _audioMuter.MuteOthers();
        } catch (Exception ex) {
            Log.Write("audio: mute others threw: " + ex.Message);
        }
    }

    private void RestoreOtherApps() {
        try {
            _audioMuter.Restore();
        } catch (Exception ex) {
            Log.Write("audio: restore others threw: " + ex.Message);
        }
    }

    private void OnMuteOthersChanged(object? sender, EventArgs e) {
        SetMuteOthers(_muteOthersItem.Checked);
    }

    private void SetToggleRecording(bool toggle) {
        if (_settings.ToggleRecording == toggle) {
            SyncRecordingModeMenu();
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon(Loc.T("Wait until dictation finishes before switching recording mode."), OverlayKind.Warning);
            SyncRecordingModeMenu();
            return;
        }
        _settings.ToggleRecording = toggle;
        _settings.Save();
        SyncRecordingModeMenu();
        UpdateStatusText();
        RebuildCommandsMenu();
        Log.Write("settings: toggleRecording=" + toggle);
        ShowBalloon(toggle
            ? Loc.T("Press a shortcut to start recording, press again to stop.")
            : Loc.T("Hold a shortcut to record, release to stop."));
    }

    private void SyncRecordingModeMenu() {
        _holdModeItem.Checked = !_settings.ToggleRecording;
        _toggleModeItem.Checked = _settings.ToggleRecording;
    }

    // ------------------------------------------------------------------
    // Tray UI
    // ------------------------------------------------------------------

    private void SetPhase(AppPhase phase) {
        var previous = _phase;
        _phase = phase;
        UpdateStatusIcon();
        if (phase == AppPhase.Listening) {
            try {
                _micMeter.Start();
                StartMicLevelPump();
                StatusOverlay.ShowListening(_web.Site.DisplayName, () => _micMeter.Level, _micMeter.CopySpectrum);
            } catch (Exception ex) {
                Log.Write("hud: listening overlay failed: " + ex.Message);
            }
        } else if (previous == AppPhase.Listening) {
            StopMicLevelPump();
            try {
                _micMeter.Stop();
            } catch (Exception ex) {
                Log.Write("audio: mic meter stop failed: " + ex.Message);
            }
            if (phase != AppPhase.Transcribing) {
                StatusOverlay.HideListening();
            }
        }

        // Progress pill: the listening HUD reshapes into it when recording stops, the
        // Generating call sites advance it (ShowProgress), and it fills up on success.
        // Failures and success notices replace it in place.
        switch (phase) {
            case AppPhase.Transcribing:
                // Leave room on the bar when a model or translation step follows.
                StatusOverlay.ShowWorking(
                    WorkStage.Transcribing, _web.Site.DisplayName,
                    cap: ExpectsReplyStep() ? 0.45f : 0.9f, restart: true);
                break;
            case AppPhase.ChoosingAction:
                StatusOverlay.HideWorking();
                break;
            case AppPhase.Idle when previous is AppPhase.Transcribing or AppPhase.Generating:
                StatusOverlay.CompleteWorking();
                break;
        }
    }

    private static void ShowProgress(WorkStage stage, string detail) =>
        StatusOverlay.ShowWorking(stage, detail, cap: 0.94f, restart: false);

    private bool ExpectsReplyStep() =>
        _activeCommand != null || _translate || _askModel
        || AutoCleanHere
        || (_selectionTask.IsCompletedSuccessfully && !string.IsNullOrEmpty(_selectionTask.Result));

    private void UpdateStatusIcon() {
        _tray.Icon = TrayIcons.For(_phase, _loggedIn, _online, _web.IsInteractive);
        NotifyUi();
    }

    private void OnLoginStateChanged(bool loggedIn) {
        _loggedIn = loggedIn;
        UpdateStatusIcon();
        UpdateLoginMenuItem();
    }

    private void OnReachabilityChanged(bool online) {
        _online = online;
        UpdateStatusIcon();
        UpdateLoginMenuItem();
    }

    private void OnInteractiveChanged(bool _) {
        UpdateStatusIcon();
        UpdateStatusText();
    }

    /// <summary>Page-load health check found the selectors stale; warn before the first failed dictation.</summary>
    private void OnMissingUiPartChanged(string? missingPart) {
        if (missingPart == null || _phase != AppPhase.Idle) {
            return;
        }
        ShowBalloon(UiChangedMessage(_web.Site.DisplayName, missingPart), OverlayKind.Warning);
    }

    private void UpdateLoginMenuItem() {
        string name = _web.Site.DisplayName;
        _loginItem.Text = !_online ? Loc.T("No internet connection — retry")
            : _loggedIn ? Loc.F("{0}: Logged In ✓ (open window)", name)
            : Loc.F("Log in to {0}…", name);
    }

    private string StatusLine() =>
        !_web.IsInteractive && _online && _loggedIn && _phase == AppPhase.Idle
            ? Loc.F("Loading {0}…", _web.Site.DisplayName)
            : Loc.F(_settings.ToggleRecording ? "Press {0} to start/stop · {1}" : "Hold {0} to dictate · {1}",
                string.Join(" / ", _settings.DictationChords.Select(HotkeyNames.For)), _web.Site.DisplayName);

    private void UpdateStatusText() {
        _statusItem.Text = StatusLine();
        _tray.Text = "EchoType — " + StatusLine();
        NotifyUi();
    }

    private void SyncModelMenu() {
        _chatgptModelItem.Text = ModelMenuLabel("ChatGPT", _settings.ChatGptSwitchVk);
        _geminiModelItem.Text = ModelMenuLabel("Gemini", _settings.GeminiSwitchVk);
        _chatgptModelItem.Checked = _settings.TranscriptionProvider == TranscriptionProvider.ChatGpt;
        _geminiModelItem.Checked = _settings.TranscriptionProvider == TranscriptionProvider.Gemini;
        _claudeModelItem.Text = ModelMenuLabel("Claude", _settings.ClaudeSwitchVk);
        _claudeModelItem.Checked = _settings.TranscriptionProvider == TranscriptionProvider.Claude;
    }

    private static string ModelMenuLabel(string name, int vk) =>
        vk <= 0 ? name : name + " (" + HotkeyNames.For(vk) + ")";

    private void SelectModel(TranscriptionProvider provider) {
        if (_settings.TranscriptionProvider == provider) {
            SyncModelMenu();
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon(Loc.T("Wait until dictation finishes before switching models."), OverlayKind.Warning);
            SyncModelMenu();
            return;
        }

        _web.SwitchSite(ChatSite.For(provider));
        _settings.TranscriptionProvider = provider;
        _settings.Save();
        _loggedIn = true;
        _engagementFailures = 0;
        SyncModelMenu();
        UpdateLoginMenuItem();
        UpdateStatusIcon();
        UpdateStatusText();
        RebuildCommandsMenu();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        ApplyReplySites();
        Log.Write("settings: transcriptionProvider=" + _settings.TranscriptionProviderName);
        ShowBalloon(Loc.F("Switched to {0}.", _web.Site.DisplayName), OverlayKind.Success);
        _ = WarmupAsync();
    }

    private void OnPressEnterAfterPasteChanged(object? sender, EventArgs e) {
        if (_settings.PressEnterAfterPaste == _pressEnterItem.Checked) {
            return;
        }
        ApplyPressEnterAfterPaste(_pressEnterItem.Checked, announce: false);
    }

    private void ApplyPressEnterAfterPaste(bool enabled, bool announce) {
        _settings.PressEnterAfterPaste = enabled;
        _settings.Save();
        if (_pressEnterItem.Checked != enabled) {
            _pressEnterItem.CheckedChanged -= OnPressEnterAfterPasteChanged;
            _pressEnterItem.Checked = enabled;
            _pressEnterItem.CheckedChanged += OnPressEnterAfterPasteChanged;
        }
        Log.Write("settings: pressEnterAfterPaste=" + enabled);
        if (announce) {
            ShowBalloon(enabled ? Loc.T("Auto Enter on") : Loc.T("Auto Enter off"));
        }
        NotifyUi();
    }

    private void UpdatePressEnterShortcutMenu() {
        _pressEnterShortcutItem.Text = _settings.PressEnterToggleVk <= 0
            ? Loc.T("Set Auto Enter shortcut…")
            : Loc.T("Auto Enter shortcut: ") + HotkeyNames.For(_settings.PressEnterToggleVk);
    }

    private void OpenPressEnterShortcutUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new ShortcutPickerForm(_settings.PressEnterToggleVk, _hotkey, _settings);
            if (form.ShowDialog() == DialogResult.OK) {
                _settings.PressEnterToggleVk = form.HotkeyVk;
                _settings.Save();
                UpdatePressEnterShortcutMenu();
                _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
                Log.Write("settings: pressEnterToggleVk=0x" + _settings.PressEnterToggleVk.ToString("X2"));
            }
        } catch (Exception ex) {
            Log.Write("shortcut ui: " + ex);
            MessageBox.Show("Could not open Auto Enter shortcut settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private void UpdateAskModelShortcutMenu() {
        _askModelShortcutItem.Text = _settings.AskModelVk <= 0
            ? Loc.T("Set Ask model shortcut…")
            : Loc.T("Ask model shortcut: ") + HotkeyNames.For(_settings.AskModelVk);
    }

    private void UpdateTranslateMenu() {
        string language = GoogleTranslation.NameFor(_settings.TranslateTargetLanguage);
        string selectionLanguage = GoogleTranslation.NameFor(_settings.TranslateSelectionTargetLanguage);
        var chord = _settings.TranslateChord;
        _translateShortcutItem.Text = chord.IsEmpty
            ? Loc.T("Set Translate shortcut…")
            : Loc.T("Translate shortcut: ") + HotkeyNames.For(chord) + " → " + language;
        var selectionChord = _settings.TranslateSelectionChord;
        _translateSelectionShortcutItem.Text = selectionChord.IsEmpty
            ? Loc.T("Set Translate selection shortcut…")
            : Loc.T("Translate selection shortcut: ") + HotkeyNames.For(selectionChord) + " → " + selectionLanguage;
        _translateLanguageRoot.Text = Loc.T("Translate speech to: ") + language;
        _translateSelectionLanguageRoot.Text = Loc.T("Translate selection to: ") + selectionLanguage;
        CheckLanguage(_translateLanguageRoot, _settings.TranslateTargetLanguage);
        CheckLanguage(_translateSelectionLanguageRoot, _settings.TranslateSelectionTargetLanguage);
    }

    private static void CheckLanguage(ToolStripMenuItem root, string code) {
        foreach (ToolStripItem item in root.DropDownItems) {
            if (item is ToolStripMenuItem mi) {
                mi.Checked = string.Equals(mi.Tag as string, code, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private void SetTranslateLanguage(string code) {
        if (string.IsNullOrWhiteSpace(code)
            || string.Equals(_settings.TranslateTargetLanguage, code, StringComparison.OrdinalIgnoreCase)) {
            return;
        }
        _settings.TranslateTargetLanguage = code;
        _settings.Save();
        UpdateTranslateMenu();
        Log.Write("settings: translateTargetLanguage=" + code);
        NotifyUi();
    }

    private void SetTranslateSelectionLanguage(string code) {
        if (string.IsNullOrWhiteSpace(code)
            || string.Equals(_settings.TranslateSelectionTargetLanguage, code, StringComparison.OrdinalIgnoreCase)) {
            return;
        }
        _settings.TranslateSelectionTargetLanguage = code;
        _settings.Save();
        UpdateTranslateMenu();
        Log.Write("settings: translateSelectionTargetLanguage=" + code);
        NotifyUi();
    }

    private void ApplyTranslateChord(HotkeyChord chord) {
        _settings.SetTranslateChord(chord);
        _settings.Save();
        UpdateTranslateMenu();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        Log.Write("settings: translate=" + HotkeyNames.For(chord));
        NotifyUi();
    }

    private void ApplyTranslateSelectionChord(HotkeyChord chord) {
        _settings.SetTranslateSelectionChord(chord);
        _settings.Save();
        UpdateTranslateMenu();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        Log.Write("settings: translate-selection=" + (chord.IsEmpty ? "(none)" : HotkeyNames.For(chord)));
        NotifyUi();
    }

    private void OpenTranslateSelectionShortcutUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new ShortcutPickerForm(
                _settings.TranslateSelectionChord,
                _hotkey,
                _settings,
                Loc.T("EchoType — Translate selection shortcut"),
                Loc.T("Shortcut (1–3 keys)"),
                Loc.F("Click the box, then press one to three keys together (for example Ctrl+Shift+Y). Select text in any app and press the shortcut: EchoType translates it to {0} and shows it in a popup, keeping its lines, blank lines and spacing. Clear removes the shortcut.",
                    GoogleTranslation.NameFor(_settings.TranslateSelectionTargetLanguage)));
            if (form.ShowDialog() == DialogResult.OK) {
                ApplyTranslateSelectionChord(form.Chord);
            }
        } catch (Exception ex) {
            Log.Write("translate-selection shortcut ui: " + ex);
            MessageBox.Show("Could not open Translate selection shortcut settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private void OpenTranslateShortcutUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            string language = GoogleTranslation.NameFor(_settings.TranslateTargetLanguage);
            using var form = new ShortcutPickerForm(
                _settings.TranslateChord,
                _hotkey,
                _settings,
                Loc.T("EchoType — Translate shortcut"),
                _settings.ToggleRecording ? Loc.T("Recording shortcut (1–3 keys)") : Loc.T("Hold-to-talk shortcut (1–3 keys)"),
                (_settings.ToggleRecording
                    ? Loc.T("Click the box, then press one to three keys together (for example Ctrl+Shift+T). Press the shortcut to start, speak, and press it again to stop.")
                    : Loc.T("Click the box, then press one to three keys together (for example Ctrl+Shift+T). Hold them and speak."))
                    + " " + Loc.F("EchoType transcribes with the selected model, translates the text to {0} with {1}, and pastes it. Change the language from Translate speech to. Clear removes the shortcut.",
                        language, OpenAiTranslation.ResolveKey(_settings).Length > 0 ? "OpenAI" : "Google Translate"));
            if (form.ShowDialog() == DialogResult.OK) {
                ApplyTranslateChord(form.Chord);
            }
        } catch (Exception ex) {
            Log.Write("translate shortcut ui: " + ex);
            MessageBox.Show("Could not open Translate shortcut settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private void OpenModelWindow() {
        if (_web.IsLoginWindowVisible) {
            _web.HideLoginWindow();
            Log.Write("model-window: shortcut hid window");
            NotifyUi();
            return;
        }
        Log.Write("model-window: shortcut opened window");
        _web.ShowLoginWindow();
        NotifyUi();
    }

    private void UpdateModelWindowShortcutMenu() {
        _modelWindowShortcutItem.Text = _settings.OpenModelWindowVk <= 0
            ? Loc.T("Set model window shortcut…")
            : Loc.T("Model window shortcut: ") + HotkeyNames.For(_settings.OpenModelWindowVk);
    }

    private void OpenModelWindowShortcutUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new ShortcutPickerForm(
                _settings.OpenModelWindowVk,
                _hotkey,
                _settings,
                _settings.OpenModelWindowVk,
                Loc.T("EchoType — Model window shortcut"),
                Loc.T("Open window key"),
                Loc.T("Click the box, then press the key. A tap opens the ChatGPT or Gemini window — the same window as Login in the tray. Tap again to close it. Clear removes the shortcut."));
            if (form.ShowDialog() == DialogResult.OK) {
                _settings.OpenModelWindowVk = form.HotkeyVk;
                _settings.Save();
                UpdateModelWindowShortcutMenu();
                _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
                Log.Write("settings: openModelWindowVk=0x" + _settings.OpenModelWindowVk.ToString("X2"));
            }
        } catch (Exception ex) {
            Log.Write("model-window shortcut ui: " + ex);
            MessageBox.Show("Could not open model window shortcut settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private void OpenAskModelShortcutUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new ShortcutPickerForm(
                _settings.AskModelVk,
                _hotkey,
                _settings,
                _settings.AskModelVk,
                Loc.T("EchoType — Ask model shortcut"),
                _settings.ToggleRecording ? Loc.T("Recording key") : Loc.T("Hold-to-talk key"),
                _settings.ToggleRecording
                    ? Loc.T("Click the box, then press the key. Press it to start, speak, and press it again to stop. EchoType sends your words to the selected model and pastes the reply — text or image — into the field you started in. Selected text is included when you have a selection. Clear removes the shortcut.")
                    : Loc.T("Click the box, then press the key. Hold it, speak, and EchoType sends your words to the selected model and pastes the reply — text or image — into the field you started in. Selected text is included when you have a selection. Clear removes the shortcut."));
            if (form.ShowDialog() == DialogResult.OK) {
                _settings.AskModelVk = form.HotkeyVk;
                _settings.Save();
                UpdateAskModelShortcutMenu();
                _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
                Log.Write("settings: askModelVk=0x" + _settings.AskModelVk.ToString("X2"));
            }
        } catch (Exception ex) {
            Log.Write("ask-model shortcut ui: " + ex);
            MessageBox.Show("Could not open Ask model shortcut settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private void UpdateModelShortcutsMenu() {
        bool any = _settings.ChatGptSwitchVk > 0 || _settings.GeminiSwitchVk > 0;
        _modelShortcutsItem.Text = any ? Loc.T("Model shortcuts…") : Loc.T("Set model shortcuts…");
    }

    private void OpenModelShortcutsUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new ModelShortcutsForm(_settings, _hotkey);
            if (form.ShowDialog() == DialogResult.OK) {
                _settings.Save();
                SyncModelMenu();
                UpdateModelShortcutsMenu();
                _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
                Log.Write("settings: chatgptSwitchVk=0x" + _settings.ChatGptSwitchVk.ToString("X2")
                    + ", geminiSwitchVk=0x" + _settings.GeminiSwitchVk.ToString("X2"));
            }
        } catch (Exception ex) {
            Log.Write("model shortcut ui: " + ex);
            MessageBox.Show("Could not open model shortcut settings: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private static void ShowBalloon(string text, OverlayKind kind = OverlayKind.Info, string? title = null) {
        StatusOverlay.ShowNotice(text, kind, title);
    }

    private async Task WarmupAsync() {
        int epoch = ++_warmupEpoch;
        try {
            var outcome = await _web.EnsureReadyAsync();
            if (epoch != _warmupEpoch) {
                return;
            }
            Log.Write("launch: warmup -> " + outcome);
            if (outcome == ReadyOutcome.RuntimeMissing) {
                ChatGPTWebController.ShowRuntimeMissingDialog();
            } else if (outcome == ReadyOutcome.LoggedOut) {
                _loggedIn = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                ShowBalloon(Loc.F("Log in to {0} before dictating.", _web.Site.DisplayName), OverlayKind.Warning);
            } else if (outcome == ReadyOutcome.Offline) {
                _online = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
            } else if (outcome == ReadyOutcome.Ready) {
                _loggedIn = true;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                UpdateStatusText();
            }
        } catch (Exception ex) {
            Log.Write("launch: warmup failed: " + ex.Message);
        }
    }

#if DEBUG
    /// <summary>Menu-driven end-to-end test without the hotkey: simulate a 5 s hold.</summary>
    private async void DebugStart() {
        if (_phase != AppPhase.Idle) {
            return;
        }
        OnHoldStart(_settings.DictationChord);
        await Task.Delay(5000);
        if (_phase == AppPhase.Listening) {
            _hotkeyHeld = false;
            _releasedAt = DateTime.UtcNow;
            await FinishListeningAsync(_session);
        }
    }
#endif

    private bool IsDictationChord(HotkeyChord chord) =>
        _settings.IsDictationChord(chord);

    /// <summary>
    /// Whether a press belongs to the running session — the same chord, or any
    /// dictation shortcut when the session is dictation (Right Ctrl starts, Ctrl+Space stops).
    /// </summary>
    private bool IsSessionChord(HotkeyChord chord) =>
        chord.Equals(_sessionChord) || (IsDictationChord(chord) && IsDictationChord(_sessionChord));

    private CustomCommand? FindCommand(HotkeyChord chord) {
        if (IsDictationChord(chord)) {
            return null;
        }
        return _settings.ActiveCommands.FirstOrDefault(c => c.Chord.Equals(chord));
    }

    private HotkeyChord[] CollectHotkeys() {
        var chords = new List<HotkeyChord>(_settings.DictationChords);
        if (_settings.AskModelVk != 0) {
            chords.Add(HotkeyChord.Single(_settings.AskModelVk));
        }
        if (!_settings.TranslateChord.IsEmpty) {
            chords.Add(_settings.TranslateChord);
        }
        if (!_settings.TranslateSelectionChord.IsEmpty) {
            chords.Add(_settings.TranslateSelectionChord);
        }
        foreach (var cmd in _settings.ActiveCommands) {
            var chord = cmd.Chord;
            if (!chord.IsEmpty) {
                chords.Add(chord);
            }
        }
        return chords.ToArray();
    }

    private uint[] CollectTapHotkeys() =>
        _settings.TapHotkeyVks.Select(vk => (uint)vk).ToArray();

    private void RebuildCommandsMenu() {
        _commandsRoot.DropDownItems.Clear();
        foreach (var cmd in _settings.ActiveCommands) {
            cmd.Normalize();
            string extra = cmd.ResolvedButtons.Count > 1
                ? Loc.F(" ({0} buttons)", cmd.ResolvedButtons.Count)
                : "";
            string label = Loc.T(_settings.RecordingVerb) + $" {HotkeyNames.For(cmd.Chord)} — {cmd.DisplayName}{extra}";
            _commandsRoot.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
        }
        if (_settings.ActiveCommands.Count > 0) {
            _commandsRoot.DropDownItems.Add(new ToolStripSeparator());
        }
        _commandsRoot.DropDownItems.Add(Loc.T("Add or edit…"), null, (_, _) => OpenCommandsUi());
        NotifyUi();
    }

    private void OpenCommandsUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new CustomCommandsForm(_settings, _hotkey, Gen.Site.DisplayName, () => {
                _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
                RebuildCommandsMenu();
            });
            IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
            form.ShowDialog(owner);
            _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
            RebuildCommandsMenu();
        } catch (Exception ex) {
            Log.Write("commands ui: " + ex);
            MessageBox.Show("Could not open custom commands: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } finally {
            _commandsUiOpen = false;
            _hotkey.CaptureKeys = false;
        }
    }

    private static void OpenLog() {
        string path = File.Exists(Log.ProjectRootFilePath) ? Log.ProjectRootFilePath!
            : File.Exists(Log.FilePath) ? Log.FilePath
            : Log.ExeDirectoryFilePath;
        try {
            if (!File.Exists(path)) {
                Log.Write("log: opened from tray (file created)");
            }
            Process.Start(new ProcessStartInfo {
                FileName = path,
                UseShellExecute = true,
            });
        } catch (Exception ex) {
            MessageBox.Show("Could not open log: " + ex.Message, "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private void NotifyUi() {
        try {
            UiChanged?.Invoke();
        } catch (Exception ex) {
            Log.Write("ui: window refresh failed: " + ex.Message);
        }
    }

    public void ShowAppWindow() {
        if (_window is not { IsDisposed: false }) {
            _window = new AppWindow(this);
        }
        _window.Reveal();
    }

    Settings IAppWindowHost.Settings => _settings;
    HotkeyMonitor IAppWindowHost.Hotkey => _hotkey;
    AppPhase IAppWindowHost.Phase => _phase;
    bool IAppWindowHost.LoggedIn => _loggedIn;
    bool IAppWindowHost.Online => _online;
    bool IAppWindowHost.PageReady => _web.IsInteractive;
    string IAppWindowHost.ModelName => _web.Site.DisplayName;
    bool IAppWindowHost.LoginWindowVisible => _web.IsLoginWindowVisible;
    float IAppWindowHost.MicLevel => _micMeter.Level;
    bool IAppWindowHost.CopyMicSpectrum(float[] dest) => _micMeter.CopySpectrum(dest);
    string IAppWindowHost.StatusText => StatusLine();

    void IAppWindowHost.SetShowAskReplyWindow(bool value) {
        if (_settings.ShowAskReplyWindow == value) {
            return;
        }
        _settings.ShowAskReplyWindow = value;
        _settings.Save();
        Log.Write("settings: showAskReplyWindow=" + value);
        NotifyUi();
    }

    /// <summary>
    /// Saves the interface language and restarts EchoType: every window, menu and HUD
    /// font is built once at startup, so a restart is what switches all of them cleanly.
    /// </summary>
    void IAppWindowHost.SetUiLanguage(string code) {
        code = Loc.Resolve(code);
        if (code == (Loc.Arabic ? Loc.ArabicCode : Loc.English)) {
            // Already showing this language; just remember the explicit choice.
            if (_settings.UiLanguage != code) {
                _settings.UiLanguage = code;
                _settings.Save();
            }
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon(Loc.T("Wait until dictation finishes before switching the language."), OverlayKind.Warning);
            NotifyUi();
            return;
        }
        _settings.UiLanguage = code;
        _settings.Save();
        Log.Write("settings: uiLanguage=" + code + ", restarting");
        if (Program.Restart()) {
            ExitThread();
        } else {
            ShowBalloon(Loc.T("Language saved. Restart EchoType to apply it."), OverlayKind.Info);
        }
    }

    string IAppWindowHost.CleanModelName => Gen.Site.DisplayName;
    bool IAppWindowHost.CleanWindowVisible => Gen.IsLoginWindowVisible;
    void IAppWindowHost.SetAutoClean(bool value) => SetAutoClean(value);
    void IAppWindowHost.SetCleanModel(TranscriptionProvider provider) => SetCleanModel(provider);
    void IAppWindowHost.ToggleCleanWindow() => ToggleCleanWindow();
    void IAppWindowHost.EditCleanPrompt() => EditCleanPrompt();
    string IAppWindowHost.AskModelName => AskWeb.Site.DisplayName;
    bool IAppWindowHost.AskWindowVisible => AskWeb.IsLoginWindowVisible;
    void IAppWindowHost.SetAskModel(TranscriptionProvider provider) => SetAskModel(provider);
    void IAppWindowHost.ToggleAskWindow() => ToggleAskWindow();
    void IAppWindowHost.EditOpenAiKey() => EditOpenAiKey();
    void IAppWindowHost.EditWordReplacements() => EditWordReplacements();
    void IAppWindowHost.EditWorkspaceModes() => EditWorkspaceModes();
    void IAppWindowHost.EditAppProfiles() => EditAppProfiles();

    void IAppWindowHost.SelectModel(TranscriptionProvider provider) => SelectModel(provider);
    void IAppWindowHost.SetToggleRecording(bool toggle) => SetToggleRecording(toggle);
    void IAppWindowHost.ToggleLoginWindow() => OpenModelWindow();
    void IAppWindowHost.OpenCommands() => OpenCommandsUi();
    void IAppWindowHost.OpenLog() => OpenLog();
    void IAppWindowHost.Quit() => ExitThread();

    void IAppWindowHost.SetMuteOthers(bool value) => SetMuteOthers(value);
    void IAppWindowHost.SetLongDictation(bool value) => SetLongDictation(value);
    void IAppWindowHost.SetTranslateLanguage(string code) => SetTranslateLanguage(code);
    void IAppWindowHost.SetTranslateSelectionLanguage(string code) => SetTranslateSelectionLanguage(code);

    void IAppWindowHost.SetPressEnterAfterPaste(bool value) =>
        ApplyPressEnterAfterPaste(value, announce: false);

    void IAppWindowHost.SetKeepTranscriptOnClipboard(bool value) {
        if (_settings.KeepTranscriptOnClipboard == value) {
            return;
        }
        _settings.KeepTranscriptOnClipboard = value;
        _settings.Save();
        Log.Write("settings: keepTranscriptOnClipboard=" + value);
        NotifyUi();
    }

    void IAppWindowHost.SetStartWithWindows(bool value) {
        if (_settings.StartWithWindows == value) {
            return;
        }
        _settings.StartWithWindows = value;
        _settings.Save();
        StartupRegistration.Apply(value);
        Log.Write("settings: startWithWindows=" + value);
        NotifyUi();
    }

    bool IAppWindowHost.TrySetTranslateChord(HotkeyChord chord) {
        string? conflict = HotkeyConflicts.Message(chord, _settings, _settings.TranslateChord);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }
        ApplyTranslateChord(chord);
        return true;
    }

    bool IAppWindowHost.TrySetTranslateSelectionChord(HotkeyChord chord) {
        string? conflict = HotkeyConflicts.Message(chord, _settings, _settings.TranslateSelectionChord);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }
        ApplyTranslateSelectionChord(chord);
        return true;
    }

    bool IAppWindowHost.TrySetDictationChord(HotkeyChord chord) {
        if (chord.IsEmpty) {
            ShowBalloon(Loc.T("Pick a dictation key — EchoType needs one to record."), OverlayKind.Warning);
            return false;
        }
        string? conflict = HotkeyConflicts.Message(chord, _settings, _settings.DictationChord);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }
        _settings.SetDictationChord(chord);
        _settings.Save();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        UpdateStatusText();
        Log.Write("settings: dictation=" + HotkeyNames.For(chord));
        return true;
    }

    bool IAppWindowHost.TrySetSecondDictationChord(HotkeyChord chord) {
        if (!chord.IsEmpty) {
            string? conflict = HotkeyConflicts.Message(chord, _settings, _settings.SecondDictationChord);
            if (conflict != null) {
                ShowBalloon(conflict, OverlayKind.Warning);
                return false;
            }
        }
        _settings.SetSecondDictationChord(chord);
        _settings.Save();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        UpdateStatusText();
        Log.Write("settings: second dictation=" + (chord.IsEmpty ? "(none)" : HotkeyNames.For(chord)));
        return true;
    }

    bool IAppWindowHost.TrySetShortcut(AppShortcut shortcut, int vk) {
        if (shortcut == AppShortcut.Translate) {
            return ((IAppWindowHost)this).TrySetTranslateChord(HotkeyChord.Single(vk));
        }
        if (shortcut == AppShortcut.TranslateSelection) {
            return ((IAppWindowHost)this).TrySetTranslateSelectionChord(HotkeyChord.Single(vk));
        }
        if (shortcut == AppShortcut.Dictation) {
            return ((IAppWindowHost)this).TrySetDictationChord(HotkeyChord.Single(vk));
        }
        if (shortcut == AppShortcut.SecondDictation) {
            return ((IAppWindowHost)this).TrySetSecondDictationChord(HotkeyChord.Single(vk));
        }
        if (shortcut == AppShortcut.ChatGpt && vk != 0 && vk == _settings.GeminiSwitchVk) {
            ShowBalloon(Loc.T("ChatGPT and Gemini cannot share the same shortcut."), OverlayKind.Warning);
            return false;
        }
        if (shortcut == AppShortcut.Gemini && vk != 0 && vk == _settings.ChatGptSwitchVk) {
            ShowBalloon(Loc.T("ChatGPT and Gemini cannot share the same shortcut."), OverlayKind.Warning);
            return false;
        }
        if ((shortcut is AppShortcut.ChatGpt or AppShortcut.Gemini) && vk != 0 && vk == _settings.ClaudeSwitchVk) {
            ShowBalloon(Loc.T("That key already switches to Claude."), OverlayKind.Warning);
            return false;
        }
        if (shortcut == AppShortcut.Claude && vk != 0
            && (vk == _settings.ChatGptSwitchVk || vk == _settings.GeminiSwitchVk)) {
            ShowBalloon(Loc.T("Each model needs its own switch shortcut."), OverlayKind.Warning);
            return false;
        }

        int ignore = shortcut switch {
            AppShortcut.AskModel => _settings.AskModelVk,
            AppShortcut.PressEnter => _settings.PressEnterToggleVk,
            AppShortcut.AutoClean => _settings.AutoCleanToggleVk,
            AppShortcut.ModelWindow => _settings.OpenModelWindowVk,
            AppShortcut.ChatGpt => _settings.ChatGptSwitchVk,
            AppShortcut.Gemini => _settings.GeminiSwitchVk,
            AppShortcut.Claude => _settings.ClaudeSwitchVk,
            _ => 0,
        };
        string? conflict = HotkeyConflicts.Message(vk, _settings, ignore);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }

        switch (shortcut) {
            case AppShortcut.AskModel:
                _settings.AskModelVk = vk;
                break;
            case AppShortcut.PressEnter:
                _settings.PressEnterToggleVk = vk;
                break;
            case AppShortcut.AutoClean:
                _settings.AutoCleanToggleVk = vk;
                break;
            case AppShortcut.ModelWindow:
                _settings.OpenModelWindowVk = vk;
                break;
            case AppShortcut.ChatGpt:
                _settings.ChatGptSwitchVk = vk;
                break;
            case AppShortcut.Gemini:
                _settings.GeminiSwitchVk = vk;
                break;
            case AppShortcut.Claude:
                _settings.ClaudeSwitchVk = vk;
                break;
        }
        _settings.Save();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        UpdatePressEnterShortcutMenu();
        UpdateAskModelShortcutMenu();
        UpdateTranslateMenu();
        UpdateModelWindowShortcutMenu();
        UpdateModelShortcutsMenu();
        SyncModelMenu();
        UpdateStatusText();
        RebuildCommandsMenu();
        Log.Write("settings: " + shortcut + "=0x" + vk.ToString("X2"));
        return true;
    }

    private void OnLongDictationChanged(object? sender, EventArgs e) {
        SetLongDictation(_longDictationItem.Checked);
    }

    private void SetLongDictation(bool value) {
        if (_settings.LongDictation == value) {
            return;
        }
        _settings.LongDictation = value;
        _settings.Save();
        if (_longDictationItem.Checked != value) {
            _longDictationItem.CheckedChanged -= OnLongDictationChanged;
            _longDictationItem.Checked = value;
            _longDictationItem.CheckedChanged += OnLongDictationChanged;
        }
        Log.Write("settings: longDictation=" + value);
        NotifyUi();
    }

    private void SetMuteOthers(bool value) {
        if (_settings.MuteOtherAppsWhileDictating == value) {
            return;
        }
        _settings.MuteOtherAppsWhileDictating = value;
        _settings.Save();
        if (_muteOthersItem.Checked != value) {
            _muteOthersItem.CheckedChanged -= OnMuteOthersChanged;
            _muteOthersItem.Checked = value;
            _muteOthersItem.CheckedChanged += OnMuteOthersChanged;
        }
        Log.Write("settings: muteOtherAppsWhileDictating=" + value);
        if (!value) {
            RestoreOtherApps();
        }
        NotifyUi();
    }

    protected override void ExitThreadCore() {
        Log.Write("shutdown");
        StopListenWatch();
        StopMicLevelPump();
        RestoreOtherApps();
        _micMeter.Dispose();
        _selectionTranslateCts?.Cancel();
        AskReplyForm.CloseCurrent();
        StatusOverlay.Shutdown();
        _hotkey.Dispose();
        _web.Dispose();
        _cleanWeb?.Dispose();
        _askWeb?.Dispose();
        if (_window is { IsDisposed: false } window) {
            window.Hide();
            window.Dispose();
        }
        _window = null;
        _tray.Visible = false; // else a ghost icon lingers until hover
        _tray.Dispose();
        _marshal.Dispose();
        TrayIcons.DisposeAll();
        _settings.Save();
        base.ExitThreadCore();
    }
}
