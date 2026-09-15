using System.Diagnostics;
using EchoType.Audio;
using EchoType.Hotkey;
using EchoType.Input;
using EchoType.Native;
using EchoType.Ui;
using EchoType.Web;

namespace EchoType;

/// <summary>
/// Owns the tray icon, the global hotkey, and the dictation state machine
/// (AppDelegate.swift analog). Recording is hold-to-talk by default, or
/// press-to-start / press-to-stop when that mode is selected in the tray.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext {

    /// <summary>Releases shorter than this are a tap → cancel (mac tapThreshold).</summary>
    private const double TapThresholdSeconds = 0.35;

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
    private uint _sessionVk;
    private PasteTarget? _pasteTarget;
    private string? _selectedText;
    private Task<string?> _selectionTask = Task.FromResult<string?>(null);
    private bool _commandsUiOpen;

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
        _modelWindowShortcutItem = new ToolStripMenuItem();
        _modelWindowShortcutItem.Click += (_, _) => OpenModelWindowShortcutUi();
        UpdateModelWindowShortcutMenu();
        _muteOthersItem = new ToolStripMenuItem("Mute other apps while dictating") {
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(modelRoot);
        menu.Items.Add(_loginItem);
        menu.Items.Add(closeLoginItem);
        menu.Items.Add(_modelWindowShortcutItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_commandsRoot);
        menu.Items.Add(_askModelShortcutItem);
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
            Icon = TrayIcons.For(_phase, _loggedIn, _online),
            Text = "EchoType — " + StatusLine(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _web.ShowLoginWindow();

        _web.LoginStateChanged += OnLoginStateChanged;
        _web.ReachabilityChanged += OnReachabilityChanged;

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

        Log.Write($"launch: EchoType for Windows started (model={_web.Site.Id}, hotkey VK=0x{_settings.HotkeyVk:X2}, askModelVk=0x{_settings.AskModelVk:X2}, openModelWindowVk=0x{_settings.OpenModelWindowVk:X2}, commands={_settings.ActiveCommands.Count}, toggleRecording={_settings.ToggleRecording}, pressEnterAfterPaste={_settings.PressEnterAfterPaste}, pressEnterToggleVk=0x{_settings.PressEnterToggleVk:X2}, chatgptSwitchVk=0x{_settings.ChatGptSwitchVk:X2}, geminiSwitchVk=0x{_settings.GeminiSwitchVk:X2}, muteOtherAppsWhileDictating={_settings.MuteOtherAppsWhileDictating})");
        _ = WarmupAsync(); // alwaysReady: load the selected model at launch (mac applyPolicyAtLaunch)
    }

    // ------------------------------------------------------------------
    // Hotkey → state machine
    // ------------------------------------------------------------------

    private void OnHoldStart(uint vk) {
        if (_commandsUiOpen) {
            return;
        }
        if (vk == (uint)_settings.HotkeyVk && ConsumeCancelDoubleTap()) {
            // In toggle mode the second press of the same recording key stops
            // listening; don't treat that as the cancel double-tap.
            if (!(_settings.ToggleRecording && _phase == AppPhase.Listening && vk == _sessionVk)) {
                if (_phase != AppPhase.Idle) {
                    _ = CancelAndResetAsync("double-tap, cancelled");
                }
                return;
            }
        }
        switch (_phase) {
            case AppPhase.Idle: {
                _hotkeyHeld = true;
                _sessionVk = vk;
                _holdStartedAt = DateTime.UtcNow;
                _releasedAt = default;
                _pasteTarget = PasteTarget.Capture();
                _activeCommand = FindCommand(vk);
                _askModel = _activeCommand == null
                    && vk != (uint)_settings.HotkeyVk
                    && _settings.AskModelVk != 0
                    && vk == (uint)_settings.AskModelVk;
                _selectedText = null;
                _selectionTask = _pasteTarget != null
                    ? SelectionCapture.CaptureAsync()
                    : Task.FromResult<string?>(null);
                Log.Write(_activeCommand != null
                    ? $"command: key down ({_activeCommand.DisplayName}, VK=0x{vk:X2})"
                    : _askModel
                        ? "ask-model: key down"
                        : "dictation: key down");
                MuteOtherAppsIfEnabled();
                int session = ++_session;
                SetPhase(_web.HasWebView ? AppPhase.Engaging : AppPhase.Waking);
                _ = StartDictationSessionAsync(session);
                break;
            }
            case AppPhase.Waking:
            case AppPhase.Engaging:
                if (_settings.ToggleRecording && vk == _sessionVk) {
                    _ = CancelAndResetAsync("toggle press while opening, cancelled");
                    break;
                }
                _hotkeyHeld = true; // re-press before ready — keep waiting
                Log.Write("dictation: second press while opening");
                break;
            case AppPhase.Listening:
                if (_settings.ToggleRecording && vk == _sessionVk) {
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

    private void OnHoldEnd(uint vk) {
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

        _engagementFailures = 0;
        Sounds.Start();
        SetPhase(AppPhase.Listening);
        _hotkey.EscSwallowActive = true;
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

        if (_hotkeyHeld) {
            return; // normal hold — wait for release
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
            Log.Write((_askModel ? "ask-model" : _activeCommand != null ? "command" : "dictation")
                + ": using selected " + _selectedText.Length + " chars");
        }

        if (transcript.Length == 0) {
            Log.Write("dictation: empty transcript");
            Sounds.Error();
            ShowBalloon(_settings.ToggleRecording
                ? "Nothing transcribed — try speaking longer before pressing the key again."
                : "Nothing transcribed — try holding the key longer.");
            ResetToIdle();
            return;
        }

        if (_activeCommand != null) {
            var buttons = _activeCommand.ResolvedButtons;
            CommandButton? chosen = buttons.Count == 1 ? buttons[0] : null;
            if (buttons.Count > 1) {
                chosen = await PickCommandButtonAsync(_activeCommand, buttons, session);
                if (!IsLive(session)) {
                    return;
                }
                if (chosen == null) {
                    Log.Write("command: action picker cancelled");
                    ResetToIdle();
                    return;
                }
            }
            if (chosen == null) {
                Log.Write("command: no action buttons");
                Sounds.Error();
                ShowBalloon("This command has no buttons. Edit it from Custom Commands.");
                ResetToIdle();
                return;
            }
            string label = buttons.Count > 1
                ? "command (" + _activeCommand.DisplayName + " / " + chosen.DisplayName + ")"
                : "command (" + _activeCommand.DisplayName + ")";
            await DeliverGeneratedReplyAsync(
                _activeCommand.BuildMessage(transcript, _selectedText, chosen),
                label,
                session);
            return;
        }

        if (_askModel) {
            string message = string.IsNullOrEmpty(_selectedText)
                ? transcript
                : SelectionCapture.BuildAskModelMessage(_selectedText, transcript);
            await DeliverGeneratedReplyAsync(message, "ask-model", session);
            return;
        }

        if (!string.IsNullOrEmpty(_selectedText)) {
            await DeliverGeneratedReplyAsync(
                SelectionCapture.BuildModelMessage(_selectedText, transcript),
                "selection-edit",
                session);
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

    private async Task DeliverGeneratedReplyAsync(string message, string logLabel, int session) {
        if (!IsLive(session)) {
            return;
        }
        SetPhase(AppPhase.Generating);
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

            if (reply.Text.Length == 0) {
                Log.Write(logLabel + ": empty reply after retries");
                Sounds.Error();
                ShowBalloon(_web.Site.DisplayName + " returned an empty reply.");
                ResetToIdle();
                return;
            }

            Log.Write(logLabel + ": delivering " + reply.Text.Length + " chars");
            DeliverPaste(reply.Text, logLabel);
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
                HandleFailure(ex);
            }
        }
    }

    private void DeliverPaste(string text, string logLabel) {
        switch (Paster.Deliver(text, _settings.KeepTranscriptOnClipboard, _settings.PressEnterAfterPaste, _pasteTarget)) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                ResetToIdle();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write(logLabel + ": no editable field focused, left on clipboard");
                Sounds.Pasted();
                ShowBalloon("Copied to clipboard — press Ctrl+V to paste.");
                ResetToIdle();
                break;
        }
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
            switch (Paster.DeliverImage(image, _settings.PressEnterAfterPaste, _pasteTarget)) {
                case Paster.Outcome.Pasted:
                    Sounds.Pasted();
                    ResetToIdle();
                    return true;
                case Paster.Outcome.CopiedToClipboard:
                    Log.Write(logLabel + ": no editable field focused, left image on clipboard");
                    Sounds.Pasted();
                    ShowBalloon("Copied image to clipboard — press Ctrl+V to paste.");
                    ResetToIdle();
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Sends the custom-command prompt and waits for a reply. A late or hung
    /// thread is recovered by opening a new chat and, if that also times out,
    /// refreshing the site and sending once more (ChatGPT and Gemini).
    /// </summary>
    private async Task<ModelReply> RequestCommandReplyAsync(string message, int session) {
        try {
            ModelReply reply = await SendCommandOnceAsync(message);
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            if (!reply.IsEmpty) {
                return reply;
            }
            Log.Write("command: empty reply");
        } catch (DriverException ex) when (IsCommandRetryable(ex)) {
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            Log.Write("command: first send failed (" + ex.Kind + ": " + ex.Message + ")");
        }

        if (!IsLive(session)) {
            return ModelReply.Empty;
        }
        Log.Write("command: retrying in a new chat");
        ShowBalloon(_web.Site.DisplayName + " is slow — retrying in a new chat…");
        try {
            ModelReply reply = await SendCommandOnceAsync(message);
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            if (!reply.IsEmpty) {
                return reply;
            }
            Log.Write("command: empty reply after new-chat retry");
        } catch (DriverException ex) when (IsCommandRetryable(ex)) {
            if (!IsLive(session)) {
                return ModelReply.Empty;
            }
            Log.Write("command: new-chat retry failed (" + ex.Kind + ": " + ex.Message + ")");
        }

        if (!IsLive(session)) {
            return ModelReply.Empty;
        }
        Log.Write("command: refreshing " + _web.Site.DisplayName + " and retrying");
        ShowBalloon("Still waiting — refreshing " + _web.Site.DisplayName + " and retrying…");
        var outcome = await _web.RefreshChatAsync();
        if (!IsLive(session)) {
            return ModelReply.Empty;
        }
        if (outcome != ReadyOutcome.Ready) {
            throw ToDriverException(outcome);
        }
        return await SendCommandOnceAsync(message);
    }

    private async Task<ModelReply> SendCommandOnceAsync(string message) {
        var driver = _web.Driver
            ?? throw new DriverException(DriverFailure.NotReady, "no driver");
        if (!await driver.StartNewChatAsync()) {
            throw new DriverException(DriverFailure.ButtonNotFound, "couldn't open a new chat");
        }
        ModelReply previous = await driver.LastAssistantReplyAsync();
        await driver.SetComposerAsync(message);
        await Task.Delay(400);
        await driver.SendPromptAsync();
        ModelReply reply = await driver.AwaitAssistantReplyAsync(previous);
        await driver.ClearComposerAsync();
        return reply;
    }

    private static bool IsCommandRetryable(DriverException ex) =>
        ex.Kind is DriverFailure.Timeout or DriverFailure.ButtonNotFound
            or DriverFailure.JavaScript or DriverFailure.NotReady;

    private DriverException ToDriverException(ReadyOutcome outcome) => outcome switch {
        ReadyOutcome.LoggedOut => new DriverException(DriverFailure.LoggedOut, "logged out"),
        ReadyOutcome.Offline => new DriverException(DriverFailure.Offline, "offline"),
        ReadyOutcome.Timeout => new DriverException(DriverFailure.Timeout, _web.Site.DisplayName + " didn't respond in time"),
        _ => new DriverException(DriverFailure.NotReady, outcome.ToString()),
    };

    private async Task CancelAndResetAsync(string reason) {
        if (_phase == AppPhase.Idle) {
            return;
        }
        Log.Write("dictation: " + reason);
        _session++;
        _hotkey.EscSwallowActive = false;
        DismissActionPicker();
        try {
            if (_web.Driver != null) {
                await _web.Driver.StopGenerationAsync();
                await _web.Driver.CancelDictationAsync();
                await _web.Driver.ClearComposerAsync();
            }
        } catch { /* best effort */ }
        ResetToIdle();
    }

    // ------------------------------------------------------------------
    // Failure handling (mac handleFailure parity)
    // ------------------------------------------------------------------

    private void HandleFailure(DriverException ex) {
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
        ShowBalloon(message);
        ResetToIdle();
    }

    private void ResetToIdle() {
        DismissActionPicker();
        RestoreOtherApps();
        _hotkeyHeld = false;
        _activeCommand = null;
        _askModel = false;
        _sessionVk = 0;
        _pasteTarget = null;
        _selectedText = null;
        _selectionTask = Task.FromResult<string?>(null);
        _hotkey.EscSwallowActive = false;
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        SetPhase(AppPhase.Idle);
        _session++;
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
        if (_settings.MuteOtherAppsWhileDictating == _muteOthersItem.Checked) {
            return;
        }
        _settings.MuteOtherAppsWhileDictating = _muteOthersItem.Checked;
        _settings.Save();
        Log.Write("settings: muteOtherAppsWhileDictating=" + _settings.MuteOtherAppsWhileDictating);
        if (!_settings.MuteOtherAppsWhileDictating) {
            RestoreOtherApps();
        }
    }

    private void SetToggleRecording(bool toggle) {
        if (_settings.ToggleRecording == toggle) {
            SyncRecordingModeMenu();
            return;
        }
        if (_phase != AppPhase.Idle) {
            ShowBalloon("Wait until dictation finishes before switching recording mode.");
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
        _phase = phase;
        UpdateStatusIcon();
    }

    private void UpdateStatusIcon() {
        _tray.Icon = TrayIcons.For(_phase, _loggedIn, _online);
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

    private void UpdateLoginMenuItem() {
        string name = _web.Site.DisplayName;
        _loginItem.Text = !_online ? "No internet connection — retry"
            : _loggedIn ? name + ": Logged In ✓ (open window)"
            : "Log in to " + name + "…";
    }

    private string StatusLine() =>
        _settings.RecordingVerb + " " + HotkeyNames.For(_settings.HotkeyVk)
            + (_settings.ToggleRecording ? " to start/stop · " : " to dictate · ")
            + _web.Site.DisplayName;

    private void UpdateStatusText() {
        _statusItem.Text = StatusLine();
        _tray.Text = "EchoType — " + StatusLine();
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
            ShowBalloon("Wait until dictation finishes before switching models.");
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
        UpdateStatusText();
        RebuildCommandsMenu();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        Log.Write("settings: transcriptionProvider=" + _settings.TranscriptionProviderName);
        ShowBalloon("Now using " + _web.Site.DisplayName + ". Sign in from the tray if needed.");
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

    private void OpenModelWindow() {
        if (_web.IsLoginWindowVisible) {
            _web.HideLoginWindow();
            Log.Write("model-window: shortcut hid window");
            return;
        }
        Log.Write("model-window: shortcut opened window");
        _web.ShowLoginWindow();
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

    private void ShowBalloon(string text) {
        _tray.BalloonTipTitle = "EchoType";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(4000);
    }

    private async Task WarmupAsync() {
        try {
            var outcome = await _web.EnsureReadyAsync();
            Log.Write("launch: warmup -> " + outcome);
            if (outcome == ReadyOutcome.RuntimeMissing) {
                ChatGPTWebController.ShowRuntimeMissingDialog();
            } else if (outcome == ReadyOutcome.LoggedOut) {
                _loggedIn = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                ShowBalloon("Log in to " + _web.Site.DisplayName + " from the tray icon before dictating.");
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
        OnHoldStart((uint)_settings.HotkeyVk);
        await Task.Delay(5000);
        if (_phase == AppPhase.Listening) {
            _hotkeyHeld = false;
            _releasedAt = DateTime.UtcNow;
            await FinishListeningAsync(_session);
        }
    }
#endif

    private CustomCommand? FindCommand(uint vk) {
        if (vk == (uint)_settings.HotkeyVk) {
            return null;
        }
        return _settings.ActiveCommands.FirstOrDefault(c => (uint)c.HotkeyVk == vk);
    }

    private uint[] CollectHotkeys() {
        var vks = new List<uint> { (uint)_settings.HotkeyVk };
        if (_settings.AskModelVk != 0) {
            vks.Add((uint)_settings.AskModelVk);
        }
        foreach (var cmd in _settings.ActiveCommands) {
            if (cmd.HotkeyVk != 0) {
                vks.Add((uint)cmd.HotkeyVk);
            }
        }
        return vks.ToArray();
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
            string label = $"{_settings.RecordingVerb} {HotkeyNames.For(cmd.HotkeyVk)} — {cmd.DisplayName}{extra}";
            _commandsRoot.DropDownItems.Add(new ToolStripMenuItem(label) { Enabled = false });
        }
        if (_settings.ActiveCommands.Count > 0) {
            _commandsRoot.DropDownItems.Add(new ToolStripSeparator());
        }
        _commandsRoot.DropDownItems.Add("Add or edit…", null, (_, _) => OpenCommandsUi());
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
            form.ShowDialog();
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

    protected override void ExitThreadCore() {
        Log.Write("shutdown");
        RestoreOtherApps();
        _hotkey.Dispose();
        _web.Dispose();
        _tray.Visible = false; // else a ghost icon lingers until hover
        _tray.Dispose();
        _marshal.Dispose();
        TrayIcons.DisposeAll();
        _settings.Save();
        base.ExitThreadCore();
    }
}
