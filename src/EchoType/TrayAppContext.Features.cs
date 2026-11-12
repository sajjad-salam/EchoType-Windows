using System.Diagnostics;
using EchoType.Health;
using EchoType.History;
using EchoType.Hotkey;
using EchoType.Input;
using EchoType.Native;
using EchoType.Translation;
using EchoType.Ui;
using EchoType.Update;
using EchoType.Web;

namespace EchoType;

/// <summary>
/// History, "paste last result", the action palette, spoken prefixes, the Health card and
/// setup wizard, model failover, updates (app + community selector fixes) and settings
/// import/export. Kept apart from the dictation state machine in TrayAppContext.cs.
/// </summary>
internal sealed partial class TrayAppContext {

    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    private System.Windows.Forms.Timer? _dailyTimer;
    private ReleaseInfo? _availableUpdate;
    private string _updateStatus = "";
    private bool _updateBusy;
    private bool _setupOpen;
    /// <summary>True while the recording was started from the action palette: it always stops on the next press.</summary>
    private bool _paletteSession;
    private DateTime _lastFailover;

    /// <summary>Hold-to-talk or press-to-start/stop for the running session (palette recordings are always press-to-stop).</summary>
    private bool SessionToggle => _settings.ToggleRecording || _paletteSession;

    /// <summary>Called once at the end of the constructor.</summary>
    private void InitFeatures() {
        TranscriptHistory.Enabled = _settings.HistoryEnabled;
        TranscriptHistory.Limit = _settings.HistoryLimit;

        // Updates and community selector fixes: shortly after launch, then once a day.
        _dailyTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _dailyTimer.Tick += (_, _) => {
            _dailyTimer.Interval = (int)UpdateInterval.TotalMilliseconds;
            _ = RunDailyChecksAsync();
        };
        _dailyTimer.Start();

        if (_settings.SetupCompleted == false) {
            // First launch: let the tray icon and model pages appear first.
            var once = new System.Windows.Forms.Timer { Interval = 2500 };
            once.Tick += (_, _) => {
                once.Dispose();
                ShowSetupWizard();
            };
            once.Start();
        }
    }

    private async Task RunDailyChecksAsync() {
        if (_settings.RemoteSelectors && await RemoteSelectors.RefreshAsync()) {
            // A page that is stuck on a changed UI picks the fix up when it reloads.
            foreach (var web in AllWebs().Where(w => w.MissingUiPart != null)) {
                Log.Write("selectors: reloading " + web.Site.DisplayName + " for the new community fixes");
                _ = web.ReloadInBackgroundAsync();
            }
        }
        if (_settings.CheckForUpdates) {
            await CheckForUpdatesAsync(manual: false);
        }
    }

    private IEnumerable<ChatGPTWebController> AllWebs() {
        yield return _web;
        foreach (var w in _modelWindows.Values) yield return w;
        if (_cleanWeb != null) yield return _cleanWeb;
        if (_askWeb != null) yield return _askWeb;
        if (_translateWeb != null) yield return _translateWeb;
    }

    // ------------------------------------------------------------------
    // History
    // ------------------------------------------------------------------

    private void RecordHistory(DictationJob? job, string kind, string transcript, string result, string model,
        bool failed = false, string note = "") {
        try {
            TranscriptHistory.Add(new HistoryEntry {
                Kind = kind,
                App = job?.PasteTarget?.ProcessName ?? "",
                Model = model,
                Transcript = transcript ?? "",
                Result = result ?? "",
                Failed = failed,
                Note = note,
            });
        } catch (Exception ex) {
            Log.Write("history: add failed: " + ex.Message);
        }
    }

    private void OpenHistory() {
        HistoryForm.ShowWindow(
            text => _ = PasteFromHistoryAsync(text),
            word => _ = AddWordReplacementAsync(fromSelection: false, presetWord: word));
    }

    /// <summary>The History window stepped aside; paste into whatever is in front now.</summary>
    private async Task PasteFromHistoryAsync(string text) {
        await Task.Delay(300);
        PasteText(text, "history");
    }

    private bool IsPasteLastChord(HotkeyChord chord) =>
        !_settings.PasteLastChord.IsEmpty && chord.Equals(_settings.PasteLastChord);

    /// <summary>"Paste last result": the newest History entry, into the focused field.</summary>
    private async Task PasteLastAsync() {
        if (RecorderCapturing) {
            ShowBalloon(Loc.T("Wait until dictation finishes before pasting."), OverlayKind.Warning);
            return;
        }
        var last = TranscriptHistory.Latest;
        if (last == null) {
            ShowBalloon(Loc.T("Nothing to paste yet — History is empty."), OverlayKind.Info);
            return;
        }
        // Synthesize Ctrl+V only once the shortcut's own modifiers are up.
        var wait = Stopwatch.StartNew();
        while (NativeMethods.AnyModifierDown() && wait.ElapsedMilliseconds < 1500) {
            await Task.Delay(15);
        }
        PasteText(last.Text, "paste-last");
    }

    private void PasteText(string text, string logLabel) {
        if (string.IsNullOrEmpty(text)) {
            return;
        }
        var target = PasteTarget.Capture();
        var result = Paster.Deliver(text, keepTranscriptOnClipboard: true, pressEnterAfterPaste: false, target);
        Log.Write(logLabel + ": " + result.Outcome + " (" + text.Length + " chars)");
        if (result.Outcome == Paster.Outcome.CopiedToClipboard) {
            ShowBalloon(Loc.T("Copied to clipboard — press Ctrl+V to paste."), OverlayKind.Success);
        } else {
            Sounds.Pasted();
        }
    }

    private void SetHistoryEnabled(bool value) {
        if (_settings.HistoryEnabled == value) return;
        _settings.HistoryEnabled = value;
        _settings.Save();
        TranscriptHistory.Enabled = value;
        Log.Write("settings: historyEnabled=" + value);
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Action palette
    // ------------------------------------------------------------------

    private bool IsActionPaletteChord(HotkeyChord chord) =>
        !_settings.ActionPaletteChord.IsEmpty && chord.Equals(_settings.ActionPaletteChord);

    private void OnActionPaletteKey() {
        if (_paletteSession) {
            if (_phase == AppPhase.Listening) {
                StopToggleListening();
                return;
            }
            if (_phase is AppPhase.Waking or AppPhase.Engaging) {
                _ = CancelAndResetAsync("palette key while opening, cancelled");
                return;
            }
        }
        if (RecorderCapturing) {
            return; // never steal focus from a recording in progress
        }
        if (ActionPaletteForm.IsOpen) {
            ActionPaletteForm.CloseCurrent();
            return;
        }
        // Remember where the user was: the chosen action runs there once the palette closes.
        var target = PasteTarget.Capture();
        ActionPaletteForm.ShowPalette(BuildPaletteActions(), action => _ = RunPaletteActionAsync(action, target));
    }

    private async Task RunPaletteActionAsync(PaletteAction action, PasteTarget? target) {
        Log.Write("palette: " + action.Title);
        if (target != null) {
            target.Restore(required: false);
            await Task.Delay(120);
        }
        try {
            action.Run();
        } catch (Exception ex) {
            Log.Write("palette: action failed: " + ex);
        }
    }

    private List<PaletteAction> BuildPaletteActions() {
        string key = HotkeyNames.For(_settings.ActionPaletteChord);
        string stopHint = Loc.F("Speak, then press {0} again to finish", key);
        string Shortcut(HotkeyChord chord) => chord.IsEmpty ? "" : HotkeyNames.For(chord);
        string Single(int vk) => vk <= 0 ? "" : HotkeyNames.For(vk);
        var list = new List<PaletteAction> {
            new(Loc.T("Dictate"), stopHint + " · " + Loc.T("with text selected it rewrites it"),
                Shortcut(_settings.DictationChord), () => StartPaletteRecording(null, ask: false, translate: false), Loc.T("Record")),
            new(Loc.F("Ask {0}", ChatSite.For(_settings.AskProvider).DisplayName), stopHint,
                Single(_settings.AskModelVk), () => StartPaletteRecording(null, ask: true, translate: false), Loc.T("Record")),
            new(Loc.F("Translate speech → {0}", GoogleTranslation.NameFor(_settings.TranslateTargetLanguage)), stopHint,
                Shortcut(_settings.TranslateChord), () => StartPaletteRecording(null, ask: false, translate: true), Loc.T("Record")),
        };
        foreach (var cmd in _settings.ActiveCommands) {
            var c = cmd;
            list.Add(new(c.DisplayName, Loc.T("Custom command") + " · " + stopHint, Shortcut(c.Chord),
                () => StartPaletteRecording(c, ask: false, translate: false), Loc.T("Command")));
        }
        list.Add(new(Loc.F("Translate selected text → {0}", GoogleTranslation.NameFor(_settings.TranslateSelectionTargetLanguage)),
            Loc.T("Select text first"), Shortcut(_settings.TranslateSelectionChord), () => _ = TranslateSelectionAsync()));
        list.Add(new(Loc.T("Paste last result"), TranscriptHistory.Latest is { } last ? Truncate(last.Text.ReplaceLineEndings(" "), 70) : Loc.T("History is empty"),
            Shortcut(_settings.PasteLastChord), () => _ = PasteLastAsync()));
        list.Add(new(Loc.T("Open history"), Loc.T("Search, copy or paste earlier results"), null, OpenHistory));
        list.Add(new(Loc.T("Add word replacement"), Loc.T("Fix a word the model keeps getting wrong"),
            Shortcut(_settings.AddWordReplacementChord), () => _ = AddWordReplacementAsync(fromSelection: true)));
        foreach (var provider in TranscriptionProviders.Transcribing) {
            var p = provider;
            string name = ChatSite.For(p).DisplayName;
            bool current = _settings.TranscriptionProvider == p;
            list.Add(new(Loc.F("Transcribe with {0}", name), current ? Loc.T("Current transcription model") : null,
                Single(p switch {
                    TranscriptionProvider.Gemini => _settings.GeminiSwitchVk,
                    TranscriptionProvider.Claude => _settings.ClaudeSwitchVk,
                    _ => _settings.ChatGptSwitchVk,
                }), () => SelectModel(p), Loc.T("Model")));
        }
        list.Add(new(_settings.AutoClean ? Loc.T("Turn Auto Clean off") : Loc.T("Turn Auto Clean on"), null,
            Single(_settings.AutoCleanToggleVk), () => SetAutoClean(!_settings.AutoClean)));
        list.Add(new(_settings.PressEnterAfterPaste ? Loc.T("Turn Auto Enter off") : Loc.T("Turn Auto Enter on"), null,
            Single(_settings.PressEnterToggleVk), () => ApplyPressEnterAfterPaste(!_settings.PressEnterAfterPaste, announce: true)));
        list.Add(new(Loc.T("Open EchoType settings"), null, null, ShowAppWindow));
        return list;
    }

    /// <summary>A recording started from the palette: press-to-stop, whatever the recording mode.</summary>
    private void StartPaletteRecording(CustomCommand? command, bool ask, bool translate) {
        if (_phase != AppPhase.Idle || _recorderJob != null) {
            ShowBalloon(Loc.T("EchoType is still busy — try again in a moment."), OverlayKind.Warning);
            return;
        }
        BeginSession(_settings.ActionPaletteChord, isDictation: false,
            new SessionStart(command, ask, translate));
    }

    /// <summary>What a palette recording is for (instead of deriving it from the pressed chord).</summary>
    private sealed record SessionStart(CustomCommand? Command, bool Ask, bool Translate);

    // ------------------------------------------------------------------
    // Spoken prefixes
    // ------------------------------------------------------------------

    /// <summary>"ask …" / "translate …" at the start of plain dictation re-routes the job.</summary>
    private string ApplyVoicePrefix(DictationJob job, string transcript, bool hasSelection) {
        if (!_settings.VoicePrefixes || job.Command != null || job.AskModel || job.Translate || hasSelection) {
            return transcript;
        }
        var (route, rest) = VoicePrefixes.Match(transcript);
        switch (route) {
            case VoiceRoute.Ask:
                Log.Write("voice-prefix: ask → Ask model");
                job.AskModel = true;
                return rest;
            case VoiceRoute.Translate:
                Log.Write("voice-prefix: translate");
                job.Translate = true;
                return rest;
            default:
                return transcript;
        }
    }

    private void SetVoicePrefixes(bool value) {
        if (_settings.VoicePrefixes == value) return;
        _settings.VoicePrefixes = value;
        _settings.Save();
        Log.Write("settings: voicePrefixes=" + value);
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Health
    // ------------------------------------------------------------------

    private IReadOnlyList<HealthItem> GetHealth() {
        var items = new List<HealthItem>();
        items.Add(SystemChecks.Microphone() switch {
            MicAccess.DeniedForAll => new HealthItem("mic", Loc.T("Microphone"),
                Loc.T("Microphone access is off in Windows privacy settings."), HealthLevel.Error, Loc.T("Open settings")),
            MicAccess.DeniedForDesktopApps => new HealthItem("mic", Loc.T("Microphone"),
                Loc.T("\"Let desktop apps access your microphone\" is off."), HealthLevel.Error, Loc.T("Open settings")),
            MicAccess.DeniedByPolicy => new HealthItem("mic", Loc.T("Microphone"),
                Loc.T("Microphone access is turned off for this device."), HealthLevel.Error, Loc.T("Open settings")),
            MicAccess.Unknown => new HealthItem("mic", Loc.T("Microphone"),
                Loc.T("Couldn't read the privacy setting — check it if dictation hears nothing."), HealthLevel.Warning, Loc.T("Open settings")),
            _ => new HealthItem("mic", Loc.T("Microphone"), Loc.T("Desktop apps may use the microphone."), HealthLevel.Ok, null),
        });
        items.Add(SystemChecks.WebView2Installed()
            ? new HealthItem("webview", Loc.T("WebView2 runtime"), Loc.T("Installed."), HealthLevel.Ok, null)
            : new HealthItem("webview", Loc.T("WebView2 runtime"), Loc.T("Missing — EchoType can't open the model pages."), HealthLevel.Error, Loc.T("Download")));

        foreach (var provider in Enum.GetValues<TranscriptionProvider>()) {
            bool current = provider == _settings.TranscriptionProvider;
            ChatGPTWebController? web = current ? _web : _modelWindows.GetValueOrDefault(provider);
            string name = ChatSite.For(provider).DisplayName;
            var item = WebHealth(ProviderId(provider), name, web);
            if (current && item.Level == HealthLevel.Ok) {
                item = item with { Status = Loc.T("Ready · transcribing your dictation") };
            }
            items.Add(item);
        }
        if (_cleanWeb != null && (_settings.AutoClean || _settings.ActiveCommands.Count > 0)) {
            items.Add(WebHealth("clean", Loc.F("{0} (cleaning)", _cleanWeb.Site.DisplayName), _cleanWeb));
        }
        if (_settings.AskModelVk != 0 || _settings.VoicePrefixes || !_settings.ActionPaletteChord.IsEmpty) {
            items.Add(WebHealth("ask", Loc.F("{0} (Ask model)", ChatSite.For(_settings.AskProvider).DisplayName), _askWeb));
        }
        if (_settings.TranslateWithChatGpt && (!_settings.TranslateChord.IsEmpty || !_settings.TranslateSelectionChord.IsEmpty)) {
            items.Add(WebHealth("translate", Loc.T("ChatGPT (translation)"), _translateWeb));
        }
        return items;
    }

    private static string ProviderId(TranscriptionProvider p) => p switch {
        TranscriptionProvider.Gemini => "gemini",
        TranscriptionProvider.Claude => "claude",
        _ => "chatgpt",
    };

    private static HealthItem WebHealth(string id, string title, ChatGPTWebController? web) {
        if (web == null || !web.HasWebView) {
            return new HealthItem(id, title, Loc.T("Not loaded yet."), HealthLevel.Pending, Loc.T("Open"));
        }
        if (!web.IsOnline) {
            return new HealthItem(id, title, Loc.T("Offline — check your internet connection."), HealthLevel.Error, Loc.T("Retry"));
        }
        if (web.LoggedIn == false) {
            return new HealthItem(id, title, Loc.T("Signed out."), HealthLevel.Error, Loc.T("Sign in"));
        }
        if (web.MissingUiPart is { } part) {
            return new HealthItem(id, title, Loc.F("The page changed — EchoType can't find its {0}.", part), HealthLevel.Error, Loc.T("Reload"));
        }
        if (web.IsInteractive) {
            return new HealthItem(id, title, Loc.T("Ready."), HealthLevel.Ok, Loc.T("Open"));
        }
        return new HealthItem(id, title, Loc.T("Loading…"), HealthLevel.Pending, Loc.T("Open"));
    }

    private void FixHealth(string id) {
        Log.Write("health: fix " + id);
        switch (id) {
            case "mic":
                SystemChecks.OpenMicrophoneSettings();
                return;
            case "webview":
                SystemChecks.OpenUri("https://developer.microsoft.com/microsoft-edge/webview2/");
                return;
            case "chatgpt":
                FixWeb(TranscriptionProvider.ChatGpt);
                return;
            case "gemini":
                FixWeb(TranscriptionProvider.Gemini);
                return;
            case "claude":
                FixWeb(TranscriptionProvider.Claude);
                return;
            case "clean":
                FixSideWeb(Gen, ToggleCleanWindow);
                return;
            case "ask":
                if (_askWeb == null) {
                    ApplyAskSite();
                }
                FixSideWeb(AskWeb, ToggleAskWindow);
                return;
            case "translate":
                FixSideWeb(TranslateWeb, () => TranslateWeb.ShowLoginWindow());
                return;
        }
    }

    private void FixWeb(TranscriptionProvider provider) {
        ChatGPTWebController? web = provider == _settings.TranscriptionProvider ? _web : _modelWindows.GetValueOrDefault(provider);
        if (web != null && web.HasWebView && (!web.IsOnline || web.MissingUiPart != null)) {
            _ = web.ReloadInBackgroundAsync();
            return;
        }
        OpenModel(provider);
    }

    private void FixSideWeb(ChatGPTWebController web, Action open) {
        if (web.HasWebView && (!web.IsOnline || web.MissingUiPart != null)) {
            _ = web.ReloadInBackgroundAsync();
            return;
        }
        if (!web.IsLoginWindowVisible) {
            open();
        }
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Failover
    // ------------------------------------------------------------------

    /// <summary>
    /// The transcription page is signed out or its UI changed: switch to another model whose
    /// page is signed in and ready, so the next recording works. Returns the new model's name.
    /// </summary>
    private string? TryFailover(DriverException ex, ChatGPTWebController web) {
        if (!_settings.AutoFailover || !ReferenceEquals(web, _web)
            || ex.Kind is not (DriverFailure.LoggedOut or DriverFailure.UiChanged)
            || IsBusyExcept()
            || DateTime.UtcNow - _lastFailover < TimeSpan.FromSeconds(30)) {
            return null;
        }
        foreach (var (provider, candidate) in _modelWindows.ToList()) {
            if (provider.CanTranscribe() && candidate.IsInteractive && candidate.IsOnline && candidate.LoggedIn != false && candidate.MissingUiPart == null) {
                string from = _web.Site.DisplayName;
                _lastFailover = DateTime.UtcNow;
                Log.Write("failover: " + from + " " + ex.Kind + ", switching to " + candidate.Site.DisplayName);
                _ = SwitchWhenIdleAsync(provider);
                return candidate.Site.DisplayName;
            }
        }
        return null;
    }

    /// <summary>Busy with anything other than the job that is failing right now.</summary>
    private bool IsBusyExcept() => RecorderCapturing || _jobs.Count > 1;

    /// <summary>The failing job is still winding down; switch once EchoType is idle (or give up).</summary>
    private async Task SwitchWhenIdleAsync(TranscriptionProvider provider) {
        var wait = Stopwatch.StartNew();
        do {
            await Task.Delay(250);
        } while (IsBusy && wait.Elapsed < TimeSpan.FromSeconds(15));
        if (IsBusy) {
            Log.Write("failover: still busy, not switching");
            return;
        }
        SelectModel(provider);
    }

    private void SetAutoFailover(bool value) {
        if (_settings.AutoFailover == value) return;
        _settings.AutoFailover = value;
        _settings.Save();
        Log.Write("settings: autoFailover=" + value);
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Updates
    // ------------------------------------------------------------------

    private async Task CheckForUpdatesAsync(bool manual) {
        if (_updateBusy) return;
        _updateBusy = true;
        _updateStatus = Loc.T("Checking for updates…");
        NotifyUi();
        try {
            var latest = await Updater.GetLatestAsync();
            if (latest == null) {
                _updateStatus = Updater.NoReleasesYet
                    ? Loc.F("You have version {0}. No release has been published on GitHub yet.", Updater.Current)
                    : Loc.T("Couldn't reach GitHub to check for updates.");
                if (manual) ShowBalloon(_updateStatus, OverlayKind.Warning);
                return;
            }
            if (latest.Version <= Updater.Current) {
                _availableUpdate = null;
                _updateStatus = Loc.F("You have the latest version ({0}).", Updater.Current);
                if (manual) ShowBalloon(_updateStatus, OverlayKind.Success);
                return;
            }
            _availableUpdate = latest;
            _updateStatus = Loc.F("Version {0} is available.", latest.Version);
            Log.Write("update: " + latest.Tag + " available (running " + Updater.Current + ")");
            if (manual || _settings.SkippedUpdateVersion != latest.Version.ToString()) {
                ShowBalloon(Loc.F("EchoType {0} is available — install it from Settings → Updates.", latest.Version), OverlayKind.Info);
            }
        } finally {
            _updateBusy = false;
            NotifyUi();
        }
    }

    private async Task InstallUpdateAsync() {
        var release = _availableUpdate;
        if (release == null || _updateBusy) return;
        if (IsBusy) {
            ShowBalloon(Loc.T("Wait until dictation finishes before updating."), OverlayKind.Warning);
            return;
        }
        if (release.ZipUrl == null) {
            SystemChecks.OpenUri(release.PageUrl);
            return;
        }
        _updateBusy = true;
        try {
            var progress = new Progress<int>(p => {
                _updateStatus = Loc.F("Downloading {0}… {1}%", release.Version, p);
                NotifyUi();
            });
            if (await Updater.DownloadAndInstallAsync(release, progress)) {
                _updateStatus = Loc.T("Installing — EchoType restarts in a moment.");
                NotifyUi();
                ExitThread();
                return;
            }
            _updateStatus = Loc.T("The update couldn't be installed automatically.");
            SystemChecks.OpenUri(release.PageUrl);
        } catch (Exception ex) {
            Log.Write("update: install failed: " + ex.Message);
            _updateStatus = Loc.T("The download failed — try again later.");
            ShowBalloon(_updateStatus, OverlayKind.Error);
        } finally {
            _updateBusy = false;
            NotifyUi();
        }
    }

    private void SkipUpdate() {
        if (_availableUpdate == null) return;
        _settings.SkippedUpdateVersion = _availableUpdate.Version.ToString();
        _settings.Save();
        _updateStatus = Loc.F("Skipped version {0}.", _availableUpdate.Version);
        _availableUpdate = null;
        NotifyUi();
    }

    private void SetCheckForUpdates(bool value) {
        if (_settings.CheckForUpdates == value) return;
        _settings.CheckForUpdates = value;
        _settings.Save();
        Log.Write("settings: checkForUpdates=" + value);
        NotifyUi();
    }

    private void SetRemoteSelectors(bool value) {
        if (_settings.RemoteSelectors == value) return;
        _settings.RemoteSelectors = value;
        _settings.Save();
        Log.Write("settings: remoteSelectors=" + value);
        if (value) {
            _ = RemoteSelectors.RefreshAsync();
        } else {
            RemoteSelectors.Delete();
        }
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Setup wizard
    // ------------------------------------------------------------------

    private void ShowSetupWizard() {
        if (_setupOpen) return;
        _setupOpen = true;
        try {
            using var wizard = new SetupWizardForm(this);
            wizard.ShowDialog();
        } catch (Exception ex) {
            Log.Write("setup wizard: " + ex);
        } finally {
            _setupOpen = false;
        }
        if (_settings.SetupCompleted != true) {
            _settings.SetupCompleted = true;
            _settings.Save();
        }
        NotifyUi();
    }

    // ------------------------------------------------------------------
    // Advanced options
    // ------------------------------------------------------------------

    private void SetProgressPillHideMs(int ms) {
        ms = Math.Clamp(ms, 0, 600_000);
        if (_settings.ProgressPillHideMs == ms) return;
        _settings.ProgressPillHideMs = ms;
        _settings.Save();
        StatusOverlay.ParkAfterMs = ms;
        Log.Write("settings: progressPillHideMs=" + ms);
    }

    private void SetOpenAiTranslationModel(string model) {
        model = (model ?? "").Trim();
        if (_settings.OpenAiTranslationModel == model) return;
        _settings.OpenAiTranslationModel = model;
        _settings.Save();
        Log.Write("settings: openAiTranslationModel=" + (model.Length == 0 ? "(default)" : model));
    }

    private void SetHistoryLimit(int limit) {
        limit = Math.Clamp(limit, 10, 5000);
        if (_settings.HistoryLimit == limit) return;
        _settings.HistoryLimit = limit;
        _settings.Save();
        TranscriptHistory.Limit = limit;
        TranscriptHistory.Trim();
        Log.Write("settings: historyLimit=" + limit);
    }

    private void SetTranslateWithChatGpt(bool value) {
        if (_settings.TranslateWithChatGpt == value) return;
        _settings.TranslateWithChatGpt = value;
        _settings.Save();
        if (_chatGptTranslateItem.Checked != value) {
            _chatGptTranslateItem.Checked = value;
        }
        Log.Write("settings: translateWithChatGpt=" + value);
        NotifyUi();
    }

    private static void OpenFolder(string path) {
        try {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        } catch (Exception ex) {
            Log.Write("open folder failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Export / import
    // ------------------------------------------------------------------

    private void ExportSettings() {
        IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
        using var dialog = new SaveFileDialog {
            Filter = Loc.T("EchoType settings") + " (*.json)|*.json",
            FileName = "echotype-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        try {
            File.WriteAllText(dialog.FileName, _settings.ToShareableJson());
            Log.Write("settings: exported to " + dialog.FileName);
            ShowBalloon(Loc.T("Settings exported. Your OpenAI API key was left out."), OverlayKind.Success);
        } catch (Exception ex) {
            Log.Write("settings: export failed: " + ex.Message);
            ShowBalloon(Loc.T("Couldn't export the settings."), OverlayKind.Error);
        }
    }

    private void ImportSettings() {
        if (IsBusy) {
            ShowBalloon(Loc.T("Wait until dictation finishes before importing."), OverlayKind.Warning);
            return;
        }
        IWin32Window? owner = _window is { Visible: true, IsDisposed: false } ? _window : null;
        using var dialog = new OpenFileDialog {
            Filter = Loc.T("EchoType settings") + " (*.json)|*.json|" + Loc.T("All files") + " (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;
        Settings? incoming;
        try {
            incoming = Settings.FromJson(File.ReadAllText(dialog.FileName));
        } catch (Exception ex) {
            Log.Write("settings: import read failed: " + ex.Message);
            incoming = null;
        }
        if (incoming == null) {
            ShowBalloon(Loc.T("That file isn't an EchoType settings file."), OverlayKind.Error);
            return;
        }
        var choice = MessageBox.Show(owner,
            Loc.T("Merge the file's custom commands, word replacements, text shortcuts, workspace modes and per-app profiles into yours?")
            + "\n\n" + Loc.T("Yes: also take its preferences (recording mode, Auto Clean, models for cleaning and Ask, languages, toggles).")
            + "\n" + Loc.T("No: lists only. Shortcuts that clash with yours are left unassigned."),
            Loc.T("Import settings"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        var summary = MergeImported(incoming);
        if (choice == DialogResult.Yes) {
            ImportPreferences(incoming);
        }
        _settings.Save();
        _textExpander.Update(_settings.TextShortcuts, _settings.TextShortcutsEnabled);
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        RebuildCommandsMenu();
        SyncRecordingModeMenu();
        SyncCleanMenu();
        SyncAskMenu();
        UpdateTranslateMenu();
        if (choice == DialogResult.Yes) {
            ApplyReplySites();
        }
        Log.Write("settings: imported " + dialog.FileName + " (" + summary + ")");
        ShowBalloon(Loc.F("Imported: {0}.", summary), OverlayKind.Success);
        NotifyUi();
    }

    private string MergeImported(Settings incoming) {
        int words = 0, texts = 0, modes = 0, commands = 0, profiles = 0;
        foreach (var rule in incoming.WordReplacements ?? []) {
            if (!string.IsNullOrWhiteSpace(rule?.From)) {
                WordReplacement.AddOrUpdate(_settings.WordReplacements, rule.From, rule.To ?? "");
                words++;
            }
        }
        foreach (var shortcut in incoming.TextShortcuts ?? []) {
            if (shortcut == null || !TextShortcut.IsValidTrigger(shortcut.Trigger ?? "")) continue;
            var existing = _settings.TextShortcuts.FirstOrDefault(s => string.Equals(s.Trigger, shortcut.Trigger, StringComparison.OrdinalIgnoreCase));
            if (existing != null) {
                existing.Text = shortcut.Text ?? "";
            } else {
                _settings.TextShortcuts.Add(new TextShortcut { Trigger = shortcut.Trigger!, Text = shortcut.Text ?? "" });
            }
            texts++;
        }
        foreach (var mode in incoming.WorkspaceModes ?? []) {
            if (mode == null || string.IsNullOrWhiteSpace(mode.Name)) continue;
            _settings.WorkspaceModes.RemoveAll(m => string.Equals(m.Name, mode.Name, StringComparison.OrdinalIgnoreCase));
            mode.Keywords ??= [];
            _settings.WorkspaceModes.Add(mode);
            modes++;
        }
        foreach (var cmd in incoming.CustomCommands ?? []) {
            if (cmd == null) continue;
            cmd.Normalize();
            _settings.CustomCommands.RemoveAll(c => c.Id == cmd.Id);
            var chord = cmd.Chord;
            if (!chord.IsEmpty && HotkeyConflicts.Message(chord, _settings, default) != null) {
                cmd.SetChord(default); // its key is taken here; assign one in Custom commands
            }
            _settings.CustomCommands.Add(cmd);
            commands++;
        }
        foreach (var profile in incoming.AppProfiles ?? []) {
            if (profile == null) continue;
            profile.Normalize();
            if (profile.Apps.Count == 0) continue;
            _settings.AppProfiles.RemoveAll(p => p.Apps.Intersect(profile.Apps, StringComparer.OrdinalIgnoreCase).Any());
            _settings.AppProfiles.Add(profile);
            profiles++;
        }
        return Loc.F("{0} commands, {1} word fixes, {2} text shortcuts, {3} workspace modes, {4} app profiles",
            commands, words, texts, modes, profiles);
    }

    private void ImportPreferences(Settings s) {
        _settings.ToggleRecording = s.ToggleRecording;
        _settings.PressEnterAfterPaste = s.PressEnterAfterPaste;
        _settings.KeepTranscriptOnClipboard = s.KeepTranscriptOnClipboard;
        _settings.MuteOtherAppsWhileDictating = s.MuteOtherAppsWhileDictating;
        _settings.LongDictation = s.LongDictation;
        _settings.AutoClean = s.AutoClean;
        if (!string.IsNullOrWhiteSpace(s.AutoCleanPrompt)) _settings.AutoCleanPrompt = s.AutoCleanPrompt;
        if (!string.IsNullOrWhiteSpace(s.CleanProviderName)) _settings.CleanProviderName = s.CleanProviderName;
        if (!string.IsNullOrWhiteSpace(s.AskProviderName)) _settings.AskProviderName = s.AskProviderName;
        if (!string.IsNullOrWhiteSpace(s.TranslateTargetLanguage)) _settings.TranslateTargetLanguage = s.TranslateTargetLanguage;
        if (!string.IsNullOrWhiteSpace(s.TranslateSelectionTargetLanguage)) _settings.TranslateSelectionTargetLanguage = s.TranslateSelectionTargetLanguage;
        _settings.TranslateWithChatGpt = s.TranslateWithChatGpt;
        _settings.ShowAskReplyWindow = s.ShowAskReplyWindow;
        _settings.TextShortcutsEnabled = s.TextShortcutsEnabled;
        _settings.VoicePrefixes = s.VoicePrefixes;
        _settings.AutoFailover = s.AutoFailover;
        _settings.HistoryEnabled = s.HistoryEnabled;
        TranscriptHistory.Enabled = s.HistoryEnabled;
        _autoCleanItem.Checked = _settings.AutoClean;
        _pressEnterItem.Checked = _settings.PressEnterAfterPaste;
        _muteOthersItem.Checked = _settings.MuteOtherAppsWhileDictating;
        _longDictationItem.Checked = _settings.LongDictation;
        _textShortcutsItem.Checked = _settings.TextShortcutsEnabled;
        _chatGptTranslateItem.Checked = _settings.TranslateWithChatGpt;
    }

    // ------------------------------------------------------------------
    // IAppWindowHost (new pages)
    // ------------------------------------------------------------------

    IReadOnlyList<HealthItem> IAppWindowHost.Health => GetHealth();
    void IAppWindowHost.FixHealth(string id) => FixHealth(id);
    void IAppWindowHost.ShowSetupWizard() => ShowSetupWizard();
    void IAppWindowHost.OpenHistory() => OpenHistory();
    void IAppWindowHost.SetHistoryEnabled(bool value) => SetHistoryEnabled(value);
    void IAppWindowHost.SetHistoryLimit(int limit) => SetHistoryLimit(limit);
    void IAppWindowHost.SetVoicePrefixes(bool value) => SetVoicePrefixes(value);
    void IAppWindowHost.SetAutoFailover(bool value) => SetAutoFailover(value);
    string IAppWindowHost.UpdateStatus => _updateStatus.Length > 0 ? _updateStatus
        : Loc.F("Version {0}. EchoType checks GitHub once a day.", Updater.Current);
    bool IAppWindowHost.UpdateAvailable => _availableUpdate != null;
    bool IAppWindowHost.UpdateBusy => _updateBusy;
    void IAppWindowHost.CheckForUpdates() => _ = CheckForUpdatesAsync(manual: true);
    void IAppWindowHost.InstallUpdate() => _ = InstallUpdateAsync();
    void IAppWindowHost.SkipUpdate() => SkipUpdate();
    void IAppWindowHost.SetCheckForUpdates(bool value) => SetCheckForUpdates(value);
    void IAppWindowHost.SetRemoteSelectors(bool value) => SetRemoteSelectors(value);
    void IAppWindowHost.SetTranslateWithChatGpt(bool value) => SetTranslateWithChatGpt(value);
    void IAppWindowHost.ToggleTranslateWindow() {
        var web = TranslateWeb;
        if (web.IsLoginWindowVisible) {
            web.HideLoginWindow();
        } else {
            web.ShowLoginWindow();
        }
        NotifyUi();
    }
    bool IAppWindowHost.TranslateWindowVisible => _translateWeb?.IsLoginWindowVisible == true;
    void IAppWindowHost.SetProgressPillHideMs(int ms) => SetProgressPillHideMs(ms);
    void IAppWindowHost.SetOpenAiTranslationModel(string model) => SetOpenAiTranslationModel(model);
    void IAppWindowHost.OpenConfigFolder() => OpenFolder(Settings.Dir);
    void IAppWindowHost.OpenSoundsFolder() => OpenFolder(Path.Combine(Settings.Dir, "Sounds"));
    void IAppWindowHost.ExportSettings() => ExportSettings();
    void IAppWindowHost.ImportSettings() => ImportSettings();

    bool IAppWindowHost.TrySetPasteLastChord(HotkeyChord chord) {
        string? conflict = HotkeyConflicts.Message(chord, _settings, _settings.PasteLastChord);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }
        _settings.SetPasteLastChord(chord);
        _settings.Save();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        Log.Write("settings: paste-last=" + (chord.IsEmpty ? "(none)" : HotkeyNames.For(chord)));
        NotifyUi();
        return true;
    }

    bool IAppWindowHost.TrySetActionPaletteChord(HotkeyChord chord) {
        string? conflict = HotkeyConflicts.Message(chord, _settings, _settings.ActionPaletteChord);
        if (conflict != null) {
            ShowBalloon(conflict, OverlayKind.Warning);
            return false;
        }
        _settings.SetActionPaletteChord(chord);
        _settings.Save();
        _hotkey.UpdateHotkeys(CollectHotkeys(), CollectTapHotkeys());
        Log.Write("settings: action-palette=" + (chord.IsEmpty ? "(none)" : HotkeyNames.For(chord)));
        NotifyUi();
        return true;
    }
}
