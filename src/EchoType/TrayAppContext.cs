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

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _loginItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _commandsRoot;
    private readonly ToolStripMenuItem _chatgptModelItem;
    private readonly ToolStripMenuItem _geminiModelItem;
    private readonly ToolStripMenuItem _modelShortcutsItem;
    private readonly ToolStripMenuItem _pressEnterItem;
    private readonly ToolStripMenuItem _pressEnterShortcutItem;
    private readonly ToolStripMenuItem _askModelShortcutItem;
    private readonly ToolStripMenuItem _translateShortcutItem;
    private readonly ToolStripMenuItem _translateLanguageRoot;
    private readonly ToolStripMenuItem _modelWindowShortcutItem;
    private readonly ToolStripMenuItem _muteOthersItem;
    private readonly ToolStripMenuItem _recordingModeRoot;
    private readonly ToolStripMenuItem _holdModeItem;
    private readonly ToolStripMenuItem _toggleModeItem;
    private readonly Control _marshal = new(); // UI-thread marshal target for the hook
    private readonly Settings _settings;
    private readonly ChatGPTWebController _web;
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
    private bool _translate;
    private HotkeyChord _sessionChord;
    private PasteTarget? _pasteTarget;
    private string? _selectedText;
    private Task<string?> _selectionTask = Task.FromResult<string?>(null);
    private bool _commandsUiOpen;
    private CancellationTokenSource? _listenWatchCts;
    private CancellationTokenSource? _micLevelCts;
    private int _listenFinishGate;
    private int _chatgptThreadSends;
    private bool _chatgptForceNewThread;
    private int _warmupEpoch;

    public TrayAppContext() {
        _settings = Settings.Load();
        _web = new ChatGPTWebController(ChatSite.For(_settings.TranscriptionProvider));
        _hotkey = new HotkeyMonitor();

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _loginItem = new ToolStripMenuItem("Open login…", null, (_, _) => _web.ShowLoginWindow());
        var closeLoginItem = new ToolStripMenuItem("Close login window", null, (_, _) => _web.HideLoginWindow());
        _chatgptModelItem = new ToolStripMenuItem("ChatGPT", null, (_, _) => SelectModel(TranscriptionProvider.ChatGpt));
        _geminiModelItem = new ToolStripMenuItem("Gemini", null, (_, _) => SelectModel(TranscriptionProvider.Gemini));
        _modelShortcutsItem = new ToolStripMenuItem();
        _modelShortcutsItem.Click += (_, _) => OpenModelShortcutsUi();
        var modelRoot = new ToolStripMenuItem("Transcription model");
        modelRoot.DropDownItems.Add(_chatgptModelItem);
        modelRoot.DropDownItems.Add(_geminiModelItem);
        modelRoot.DropDownItems.Add(new ToolStripSeparator());
        modelRoot.DropDownItems.Add(_modelShortcutsItem);
        _commandsRoot = new ToolStripMenuItem("Custom Commands");
        _pressEnterItem = new ToolStripMenuItem("Press Enter after paste") {
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
        _translateLanguageRoot = new ToolStripMenuItem();
        foreach (var (code, name) in GoogleTranslation.Languages) {
            string c = code;
            _translateLanguageRoot.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) => SetTranslateLanguage(c)) {
                Tag = c,
            });
        }
        UpdateTranslateMenu();
        _modelWindowShortcutItem = new ToolStripMenuItem();
        _modelWindowShortcutItem.Click += (_, _) => OpenModelWindowShortcutUi();
        UpdateModelWindowShortcutMenu();
        _muteOthersItem = new ToolStripMenuItem("Pause or mute other apps while dictating") {
            CheckOnClick = true,
            Checked = _settings.MuteOtherAppsWhileDictating,
        };
        _muteOthersItem.CheckedChanged += OnMuteOthersChanged;
        _holdModeItem = new ToolStripMenuItem("Hold to talk", null, (_, _) => SetToggleRecording(false));
        _toggleModeItem = new ToolStripMenuItem("Press to start/stop", null, (_, _) => SetToggleRecording(true));
        _recordingModeRoot = new ToolStripMenuItem("Recording mode");
        _recordingModeRoot.DropDownItems.Add(_holdModeItem);
        _recordingModeRoot.DropDownItems.Add(_toggleModeItem);
        SyncRecordingModeMenu();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add("Open EchoType", null, (_, _) => ShowAppWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(modelRoot);
        menu.Items.Add(_loginItem);
        menu.Items.Add(closeLoginItem);
        menu.Items.Add(_modelWindowShortcutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_commandsRoot);
        menu.Items.Add(_askModelShortcutItem);
        menu.Items.Add(_translateShortcutItem);
        menu.Items.Add(_translateLanguageRoot);
        menu.Items.Add(_recordingModeRoot);
        menu.Items.Add(_pressEnterItem);
        menu.Items.Add(_pressEnterShortcutItem);
        menu.Items.Add(_muteOthersItem);
#if DEBUG
        menu.Items.Add(new ToolStripMenuItem("Start dictation (debug)", null, (_, _) => DebugStart()));
#endif
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit EchoType", null, (_, _) => ExitThread());
        RebuildCommandsMenu();
        SyncModelMenu();
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

        // BeginInvoke on this control is how the keyboard hook reaches the UI
        // thread. A Control with no HWND throws, which kills the hook — Right
        // Ctrl then appears to do nothing. Force the handle before installing.
        _ = _marshal.Handle;

        if (!_hotkey.Start(CollectHotkeys(), CollectTapHotkeys(), _marshal)) {
            MessageBox.Show("EchoType could not install its global keyboard hook.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _hotkey.HoldStart += OnHoldStart;
        _hotkey.HoldEnd += OnHoldEnd;
        _hotkey.TapPressed += OnTapPressed;
        _hotkey.CancelRequested += OnCancelRequested;

        Log.Write($"launch: EchoType for Windows started (model={_web.Site.Id}, hotkey VK=0x{_settings.HotkeyVk:X2}, askModelVk=0x{_settings.AskModelVk:X2}, translate={HotkeyNames.For(_settings.TranslateChord)}, translateTo={_settings.TranslateTargetLanguage}, openModelWindowVk=0x{_settings.OpenModelWindowVk:X2}, commands={_settings.ActiveCommands.Count}, toggleRecording={_settings.ToggleRecording}, pressEnterAfterPaste={_settings.PressEnterAfterPaste}, pressEnterToggleVk=0x{_settings.PressEnterToggleVk:X2}, chatgptSwitchVk=0x{_settings.ChatGptSwitchVk:X2}, geminiSwitchVk=0x{_settings.GeminiSwitchVk:X2}, muteOtherAppsWhileDictating={_settings.MuteOtherAppsWhileDictating})");
        StartupRegistration.Apply(_settings.StartWithWindows);
        // Stay in the tray on launch. Double-click the icon (or Open EchoType) to show the window.
        _ = WarmupAsync(); // alwaysReady: load the selected model at launch (mac applyPolicyAtLaunch)
    }

    // ------------------------------------------------------------------
    // Hotkey → state machine
    // ------------------------------------------------------------------

    private void OnHoldStart(HotkeyChord chord) {
        if (_commandsUiOpen) {
            return;
        }
        bool isDictation = IsDictationChord(chord);
        if (isDictation && ConsumeCancelDoubleTap()) {
            // In toggle mode the second press of the same recording key stops
            // listening; don't treat that as the cancel double-tap.
            if (!(_settings.ToggleRecording && _phase == AppPhase.Listening && chord.Equals(_sessionChord))) {
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
                if (_settings.ToggleRecording && chord.Equals(_sessionChord)) {
                    _ = CancelAndResetAsync("toggle press while opening, cancelled");
                    break;
                }
                _hotkeyHeld = true; // re-press before ready — keep waiting
                Log.Write("dictation: second press while opening");
                break;
            case AppPhase.Listening:
                if (_settings.ToggleRecording && chord.Equals(_sessionChord)) {
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
        if (_settings.ToggleRecording) {
            // Release does not stop recording; the next press of the same key does.
            return;
        }
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        double held = HeldSeconds();

        switch (_phase) {
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
        if (held >= TapThresholdSeconds) {
            _ = FinishListeningAsync(_session);
        } else {
            _ = CancelAndResetAsync("toggle tap too short, cancelled");
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
        if (_settings.ChatGptSwitchVk != 0 && vk == (uint)_settings.ChatGptSwitchVk) {
            SelectModel(TranscriptionProvider.ChatGpt);
            return;
        }
        if (_settings.GeminiSwitchVk != 0 && vk == (uint)_settings.GeminiSwitchVk) {
            SelectModel(TranscriptionProvider.Gemini);
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

    private async Task FinishListeningAsync(int session) {
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

        string transcript;
        try {
            await driver.SubmitDictationAsync();
            if (!IsLive(session)) {
                return;
            }
            transcript = await driver.AwaitTranscriptAsync(TimeSpan.FromSeconds(HeldSeconds()));
            if (!IsLive(session)) {
                return;
            }
            await driver.ClearComposerAsync();
        } catch (DriverException ex) {
            if (!IsLive(session)) {
                return;
            }
            // mac collectTranscript failure parity: cancel + clear, then report.
            try { await driver.CancelDictationAsync(); } catch { /* best effort */ }
            try { await driver.ClearComposerAsync(); } catch { /* best effort */ }
            if (IsLive(session)) {
                HandleFailure(ex);
            }
            return;
        }
        if (!IsLive(session)) {
            return;
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

        if (transcript.Length == 0) {
            Log.Write("dictation: empty transcript");
            Sounds.Error();
            ShowBalloon(_settings.ToggleRecording
                ? "Nothing transcribed — try speaking longer before pressing the key again."
                : "Nothing transcribed — try holding the key longer.", OverlayKind.Error);
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
                    ShowBalloon("Cancelled. Transcript copied to clipboard.", OverlayKind.Info);
                    ResetToIdle();
                    return;
                }
            }
            if (chosen == null) {
                Log.Write("command: no action buttons");
                Sounds.Error();
                ShowBalloon("This command has no buttons. Edit it from Custom Commands.", OverlayKind.Error);
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
            string message = string.IsNullOrEmpty(_selectedText)
                ? transcript
                : SelectionCapture.BuildAskModelMessage(_selectedText, transcript);
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

        Log.Write($"dictation: delivering {transcript.Length} chars");
        DeliverPaste(transcript, "dictation");
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
        CopyTranscript(transcript);
        Log.Write(logLabel + ": sending " + message.Length + " chars");

        try {
            ModelReply reply = await RequestCommandReplyAsync(message, session);
            if (!IsLive(session)) {
                return;
            }
            if (reply.Images.Count > 0) {
                Log.Write(logLabel + ": reply has " + reply.Images.Count + " image(s)");
                byte[]? imageBytes = null;
                try {
                    if (_web.Driver != null) {
                        imageBytes = await _web.Driver.DownloadBestAssistantImageAsync(reply);
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
                    _web.Site.DisplayName + " returned an empty reply. Transcript copied to clipboard.",
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
                if (_web.Driver != null) {
                    await _web.Driver.ClearComposerAsync();
                }
            } catch { /* best effort */ }
            if (IsLive(session)) {
                HandleFailure(ex, transcript);
            }
        }
    }

    /// <summary>
    /// Translate shortcut: the model page only transcribed; Google Translate (GTranslate)
    /// does the translation directly, so there is no wait for a model reply. On failure
    /// the original transcript is left on the clipboard.
    /// </summary>
    private async Task DeliverTranslationAsync(string transcript, int session) {
        if (!IsLive(session)) {
            return;
        }
        SetPhase(AppPhase.Generating);
        CopyTranscript(transcript);
        string target = _settings.TranslateTargetLanguage;
        Log.Write("translate: sending " + transcript.Length + " chars to Google Translate (to " + target + ")");

        string translated;
        try {
            translated = (await GoogleTranslation.TranslateAsync(transcript, target)).Trim();
        } catch (Exception ex) {
            if (!IsLive(session)) {
                return;
            }
            Log.Write("translate: failed: " + ex.Message);
            Sounds.Error();
            ShowBalloon("Translation failed — transcript copied to clipboard.", OverlayKind.Error);
            ResetToIdle();
            return;
        }
        if (!IsLive(session)) {
            return;
        }
        if (translated.Length == 0) {
            Log.Write("translate: empty translation");
            Sounds.Error();
            ShowBalloon("Google Translate returned nothing. Transcript copied to clipboard.", OverlayKind.Error);
            ResetToIdle();
            return;
        }

        Log.Write("translate: delivering " + translated.Length + " chars");
        Paster.DeliverResult result = Paster.Deliver(
            translated, keepTranscriptOnClipboard: true, _settings.PressEnterAfterPaste, _pasteTarget);
        switch (result.Outcome) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                if (_settings.KeepTranscriptOnClipboard) {
                    Paster.CopyLater(transcript);
                }
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write("translate: no editable field focused, left translation on clipboard");
                Sounds.Pasted();
                ShowBalloon("Copied the translation — press Ctrl+V to paste.", OverlayKind.Success);
                break;
        }
        ResetToIdle();
    }

    private void DeliverPaste(string text, string logLabel) {
        Paster.DeliverResult result = Paster.Deliver(
            text, _settings.KeepTranscriptOnClipboard, _settings.PressEnterAfterPaste, _pasteTarget);
        switch (result.Outcome) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                if (result.KeptUserFocus) {
                    ShowBalloon("Pasted into the original field.", OverlayKind.Success);
                }
                ResetToIdle();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write(logLabel + ": no editable field focused, left on clipboard");
                Sounds.Pasted();
                ShowBalloon("Copied to clipboard — press Ctrl+V to paste.", OverlayKind.Success);
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
            reply, keepTranscriptOnClipboard: true, _settings.PressEnterAfterPaste, _pasteTarget);
        switch (result.Outcome) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                Paster.CopyLater(transcript);
                ShowBalloon("Pasted the reply. Transcript copied to clipboard.", OverlayKind.Success);
                ResetToIdle();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write(logLabel + ": no editable field focused, left reply on clipboard");
                Sounds.Pasted();
                ShowBalloon("Copied the reply — press Ctrl+V to paste.", OverlayKind.Success);
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
            Paster.DeliverResult result = Paster.DeliverImage(image, _settings.PressEnterAfterPaste, _pasteTarget);
            switch (result.Outcome) {
                case Paster.Outcome.Pasted:
                    Sounds.Pasted();
                    ShowBalloon("Pasted image. Transcript copied to clipboard.", OverlayKind.Success);
                    ResetToIdle();
                    return true;
                case Paster.Outcome.CopiedToClipboard:
                    Log.Write(logLabel + ": no editable field focused, left image on clipboard");
                    Sounds.Pasted();
                    ShowBalloon("Copied image. Transcript copied to clipboard.", OverlayKind.Success);
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
        TimeSpan firstByteTimeout = CommandFirstByteTimeout();
        Log.Write("command: waiting up to " + ((int)firstByteTimeout.TotalSeconds)
            + "s for a reply (recording " + HeldSeconds().ToString("0.0") + "s)");
        bool firstNeedsNewChat = ShouldStartNewChat();
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
        ShowBalloon(_web.Site.DisplayName + " is slow — retrying in a new chat…", OverlayKind.Warning);
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
        Log.Write("command: refreshing " + _web.Site.DisplayName + " and retrying");
        ShowBalloon("Still waiting — refreshing " + _web.Site.DisplayName + " and retrying…", OverlayKind.Warning);
        var outcome = await _web.RefreshChatAsync();
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
        var driver = _web.Driver
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
        await driver.SendPromptAsync();
        ModelReply reply;
        try {
            reply = await driver.AwaitAssistantReplyAsync(previous, firstByteTimeout);
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
        if (_chatgptForceNewThread) {
            Log.Write("command: ChatGPT thread needs a new chat after an error");
            return true;
        }
        if (_chatgptThreadSends >= ChatGptThreadReuseLimit) {
            Log.Write("command: ChatGPT thread reached " + ChatGptThreadReuseLimit + " sends, opening a new chat");
            return true;
        }
        Log.Write("command: reusing ChatGPT thread (" + (_chatgptThreadSends + 1) + "/" + ChatGptThreadReuseLimit + ")");
        return false;
    }

    private void NoteChatGptThreadSuccess(bool openedNewChat) {
        if (!IsChatGptSite) {
            return;
        }
        _chatgptForceNewThread = false;
        _chatgptThreadSends = openedNewChat ? 1 : _chatgptThreadSends + 1;
    }

    private void MarkChatGptThreadFailed() {
        if (!IsChatGptSite) {
            return;
        }
        _chatgptForceNewThread = true;
    }

    private bool IsChatGptSite => _web.Site.Id == ChatSite.ChatGpt.Id;

    private static bool IsCommandRetryable(DriverException ex) =>
        ex.Kind is DriverFailure.Timeout or DriverFailure.ButtonNotFound
            or DriverFailure.JavaScript or DriverFailure.NotReady;

    private DriverException ToDriverException(ReadyOutcome outcome) => outcome switch {
        ReadyOutcome.LoggedOut => new DriverException(DriverFailure.LoggedOut, "logged out"),
        ReadyOutcome.Offline => new DriverException(DriverFailure.Offline, "offline"),
        ReadyOutcome.Timeout => new DriverException(DriverFailure.Timeout, _web.Site.DisplayName + " didn't respond in time"),
        _ => new DriverException(DriverFailure.NotReady, outcome.ToString()),
    };

    private Task CancelAndResetAsync(string reason) {
        if (_phase == AppPhase.Idle) {
            return Task.CompletedTask;
        }
        Log.Write("dictation: " + reason);
        StopListenWatch();
        _session++;
        _hotkey.EscSwallowActive = false;
        DismissActionPicker();
        try {
            _web.Driver?.InvalidateWaits();
        } catch { /* best effort */ }
        _engagementFailures = 0;
        // Cancel leaves ChatGPT/Gemini's mic UI wedged often enough that the next
        // recording does not start until a later self-heal reload. Reload now so
        // the next hotkey waits on a fresh page instead of clicking a stuck mic.
        _web.ReloadAfterCancel();
        ResetToIdle();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Failure handling (mac handleFailure parity)
    // ------------------------------------------------------------------

    private void HandleFailure(DriverException ex, string? transcriptToCopy = null) {
        string message;
        switch (ex.Kind) {
            case DriverFailure.LoggedOut:
                message = "Logged out — open the EchoType menu to log in";
                _loggedIn = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                _web.ShowLoginWindow();
                break;
            case DriverFailure.Offline:
                message = "No internet connection — check your network";
                _online = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                break;
            case DriverFailure.Timeout:
                message = _web.Site.DisplayName + " didn't respond in time";
                break;
            case DriverFailure.ButtonNotFound:
                // The page wedges occasionally; only reload after two failures in a row.
                _engagementFailures++;
                if (_engagementFailures >= 2) {
                    message = $"Dictation glitched ({ex.Message}) — reloading {_web.Site.DisplayName}, try again";
                    Log.Write($"dictation: {_engagementFailures} engagement failures, reloading webview to self-heal");
                    _engagementFailures = 0;
                    _ = _web.ReloadInBackgroundAsync();
                } else {
                    message = ex.Message.Contains("send", StringComparison.OrdinalIgnoreCase)
                        ? "Couldn't send the prompt — try again"
                        : ex.Message.Contains("new chat", StringComparison.OrdinalIgnoreCase)
                            ? "Couldn't start a new chat — try again"
                            : "Dictation didn't start — try again";
                }
                break;
            case DriverFailure.NotReady:
                message = _web.Site.DisplayName + " isn't ready yet";
                break;
            default:
                message = "Page error: " + Truncate(ex.Message, 60);
                break;
        }

        Log.Write("dictation: failed — " + message);
        Sounds.Error();
        if (!string.IsNullOrEmpty(transcriptToCopy) && CopyTranscript(transcriptToCopy)) {
            message += " Transcript copied to clipboard.";
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

    private void ResetToIdle() {
        StopListenWatch();
        DismissActionPicker();
        RestoreOtherApps();
        _hotkeyHeld = false;
        _activeCommand = null;
        _askModel = false;
        _translate = false;
        _sessionChord = default;
        _pasteTarget = null;
        _selectedText = null;
        _selectionTask = Task.FromResult<string?>(null);
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

        Log.Write("dictation: recording cut off (" + reason + ")");
        Sounds.RecordingStopped();
        ShowBalloon(
            _web.Site.DisplayName + " ended the audio. Only what you already said will be transcribed.",
            OverlayKind.Warning,
            "Recording stopped");
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        await FinishListeningAsync(session);
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
            ShowBalloon("Wait until dictation finishes before switching recording mode.", OverlayKind.Warning);
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
            ? "Press a shortcut to start recording, press again to stop."
            : "Hold a shortcut to record, release to stop.");
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
                StatusOverlay.ShowListening(_web.Site.DisplayName, () => _micMeter.Level);
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
            StatusOverlay.HideListening();
        }
    }

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

    private void UpdateLoginMenuItem() {
        string name = _web.Site.DisplayName;
        _loginItem.Text = !_online ? "No internet connection — retry"
            : _loggedIn ? name + ": Logged In ✓ (open window)"
            : "Log in to " + name + "…";
    }

    private string StatusLine() =>
        !_web.IsInteractive && _online && _loggedIn && _phase == AppPhase.Idle
            ? "Loading " + _web.Site.DisplayName + "…"
            : _settings.RecordingVerb + " " + HotkeyNames.For(_settings.HotkeyVk)
                + (_settings.ToggleRecording ? " to start/stop · " : " to dictate · ")
                + _web.Site.DisplayName;

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
    }

    private static string ModelMenuLabel(string name, int vk) =>
        vk <= 0 ? name : name + " (" + HotkeyNames.For(vk) + ")";

    private void SelectModel(TranscriptionProvider provider) {
        if (_settings.TranscriptionProvider == provider) {
            SyncModelMenu();
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon("Wait until dictation finishes before switching models.", OverlayKind.Warning);
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
        Log.Write("settings: transcriptionProvider=" + _settings.TranscriptionProviderName);
        ShowBalloon("Switched to " + _web.Site.DisplayName + ".", OverlayKind.Success);
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
            ShowBalloon(enabled ? "Auto Enter on" : "Auto Enter off");
        }
        NotifyUi();
    }

    private void UpdatePressEnterShortcutMenu() {
        _pressEnterShortcutItem.Text = _settings.PressEnterToggleVk <= 0
            ? "Set Auto Enter shortcut…"
            : "Auto Enter shortcut: " + HotkeyNames.For(_settings.PressEnterToggleVk);
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
            ? "Set Ask model shortcut…"
            : "Ask model shortcut: " + HotkeyNames.For(_settings.AskModelVk);
    }

    private void UpdateTranslateMenu() {
        string language = GoogleTranslation.NameFor(_settings.TranslateTargetLanguage);
        var chord = _settings.TranslateChord;
        _translateShortcutItem.Text = chord.IsEmpty
            ? "Set Translate shortcut…"
            : "Translate shortcut: " + HotkeyNames.For(chord) + " → " + language;
        _translateLanguageRoot.Text = "Translate to: " + language;
        foreach (ToolStripItem item in _translateLanguageRoot.DropDownItems) {
            if (item is ToolStripMenuItem mi) {
                mi.Checked = string.Equals(mi.Tag as string, _settings.TranslateTargetLanguage,
                    StringComparison.OrdinalIgnoreCase);
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

    private void ApplyTranslateChord(HotkeyChord chord) {
        _settings.SetTranslateChord(chord);
        _settings.Save();
        UpdateTranslateMenu();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        Log.Write("settings: translate=" + HotkeyNames.For(chord));
        NotifyUi();
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
                "EchoType — Translate shortcut",
                _settings.ToggleRecording ? "Recording shortcut (1–3 keys)" : "Hold-to-talk shortcut (1–3 keys)",
                (_settings.ToggleRecording
                    ? "Click the box, then press one to three keys together (for example Ctrl+Shift+T). Press the shortcut to start, speak, and press it again to stop."
                    : "Click the box, then press one to three keys together (for example Ctrl+Shift+T). Hold them and speak.")
                    + " EchoType transcribes with the selected model, translates the text to " + language
                    + " with Google Translate, and pastes it. Change the language from Translate to. Clear removes the shortcut.");
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
            ? "Set model window shortcut…"
            : "Model window shortcut: " + HotkeyNames.For(_settings.OpenModelWindowVk);
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
                "EchoType — Model window shortcut",
                "Open window key",
                "Click the box, then press the key. A tap opens the ChatGPT or Gemini window — the same window as Login in the tray. Tap again to close it. Clear removes the shortcut.");
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
                "EchoType — Ask model shortcut",
                _settings.ToggleRecording ? "Recording key" : "Hold-to-talk key",
                _settings.ToggleRecording
                    ? "Click the box, then press the key. Press it to start, speak, and press it again to stop. EchoType sends your words to the selected model and pastes the reply — text or image — into the field you started in. Selected text is included when you have a selection. Clear removes the shortcut."
                    : "Click the box, then press the key. Hold it, speak, and EchoType sends your words to the selected model and pastes the reply — text or image — into the field you started in. Selected text is included when you have a selection. Clear removes the shortcut.");
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
        _modelShortcutsItem.Text = any ? "Model shortcuts…" : "Set model shortcuts…";
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
                ShowBalloon("Log in to " + _web.Site.DisplayName + " before dictating.", OverlayKind.Warning);
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
        OnHoldStart(HotkeyChord.Single(_settings.HotkeyVk));
        await Task.Delay(5000);
        if (_phase == AppPhase.Listening) {
            _hotkeyHeld = false;
            _releasedAt = DateTime.UtcNow;
            await FinishListeningAsync(_session);
        }
    }
#endif

    private bool IsDictationChord(HotkeyChord chord) =>
        chord.Count == 1 && chord.K1 == (uint)_settings.HotkeyVk;

    private CustomCommand? FindCommand(HotkeyChord chord) {
        if (IsDictationChord(chord)) {
            return null;
        }
        return _settings.ActiveCommands.FirstOrDefault(c => c.Chord.Equals(chord));
    }

    private HotkeyChord[] CollectHotkeys() {
        var chords = new List<HotkeyChord> { HotkeyChord.Single(_settings.HotkeyVk) };
        if (_settings.AskModelVk != 0) {
            chords.Add(HotkeyChord.Single(_settings.AskModelVk));
        }
        if (!_settings.TranslateChord.IsEmpty) {
            chords.Add(_settings.TranslateChord);
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
                ? " (" + cmd.ResolvedButtons.Count + " buttons)"
                : "";
            string label = $"{_settings.RecordingVerb} {HotkeyNames.For(cmd.Chord)} — {cmd.DisplayName}{extra}";
            _commandsRoot.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
        }
        if (_settings.ActiveCommands.Count > 0) {
            _commandsRoot.DropDownItems.Add(new ToolStripSeparator());
        }
        _commandsRoot.DropDownItems.Add("Add or edit…", null, (_, _) => OpenCommandsUi());
        NotifyUi();
    }

    private void OpenCommandsUi() {
        if (_commandsUiOpen) {
            return;
        }
        _commandsUiOpen = true;
        try {
            using var form = new CustomCommandsForm(_settings, _hotkey, _web.Site.DisplayName, () => {
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
    string IAppWindowHost.StatusText => StatusLine();

    void IAppWindowHost.SelectModel(TranscriptionProvider provider) => SelectModel(provider);
    void IAppWindowHost.SetToggleRecording(bool toggle) => SetToggleRecording(toggle);
    void IAppWindowHost.ToggleLoginWindow() => OpenModelWindow();
    void IAppWindowHost.OpenCommands() => OpenCommandsUi();
    void IAppWindowHost.OpenLog() => OpenLog();
    void IAppWindowHost.Quit() => ExitThread();

    void IAppWindowHost.SetMuteOthers(bool value) => SetMuteOthers(value);
    void IAppWindowHost.SetTranslateLanguage(string code) => SetTranslateLanguage(code);

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

    bool IAppWindowHost.TrySetShortcut(AppShortcut shortcut, int vk) {
        if (shortcut == AppShortcut.Translate) {
            return ((IAppWindowHost)this).TrySetTranslateChord(HotkeyChord.Single(vk));
        }
        if (shortcut == AppShortcut.Dictation && vk <= 0) {
            ShowBalloon("Pick a dictation key — EchoType needs one to record.", OverlayKind.Warning);
            return false;
        }
        if (shortcut == AppShortcut.ChatGpt && vk != 0 && vk == _settings.GeminiSwitchVk) {
            ShowBalloon("ChatGPT and Gemini cannot share the same shortcut.", OverlayKind.Warning);
            return false;
        }
        if (shortcut == AppShortcut.Gemini && vk != 0 && vk == _settings.ChatGptSwitchVk) {
            ShowBalloon("ChatGPT and Gemini cannot share the same shortcut.", OverlayKind.Warning);
            return false;
        }

        int ignore = shortcut switch {
            AppShortcut.Dictation => _settings.HotkeyVk,
            AppShortcut.AskModel => _settings.AskModelVk,
            AppShortcut.PressEnter => _settings.PressEnterToggleVk,
            AppShortcut.ModelWindow => _settings.OpenModelWindowVk,
            AppShortcut.ChatGpt => _settings.ChatGptSwitchVk,
            AppShortcut.Gemini => _settings.GeminiSwitchVk,
            _ => 0,
        };
        string? conflict = HotkeyConflicts.Message(vk, _settings, ignore);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }

        switch (shortcut) {
            case AppShortcut.Dictation:
                _settings.HotkeyVk = vk;
                break;
            case AppShortcut.AskModel:
                _settings.AskModelVk = vk;
                break;
            case AppShortcut.PressEnter:
                _settings.PressEnterToggleVk = vk;
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
        StatusOverlay.Shutdown();
        _hotkey.Dispose();
        _web.Dispose();
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
