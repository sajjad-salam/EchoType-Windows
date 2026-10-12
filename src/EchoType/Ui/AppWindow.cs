using EchoType.Hotkey;
using EchoType.Native;
using EchoType.Translation;

namespace EchoType.Ui;

internal enum AppShortcut {
    Dictation,
    AskModel,
    Translate,
    PressEnter,
    AutoClean,
    ModelWindow,
    ChatGpt,
    Gemini,
    Claude,
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
    void EditWorkspaceModes();
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
    void SetToggleRecording(bool toggle);
    void SetMuteOthers(bool value);
    void SetTranslateLanguage(string code);
    void SetPressEnterAfterPaste(bool value);
    void SetKeepTranscriptOnClipboard(bool value);
    void SetStartWithWindows(bool value);
    bool TrySetShortcut(AppShortcut shortcut, int vk);
    bool TrySetTranslateChord(HotkeyChord chord);
    void ToggleLoginWindow();
    void OpenCommands();
    void OpenLog();
    void Quit();
}

/// <summary>
/// Main EchoType window — the same dark language as the HUD, with every setting that
/// used to live only in the tray. A sidebar switches between short pages so each group
/// of settings fits without scrolling.
/// </summary>
internal sealed class AppWindow : Form {

    private const int SidebarWidthDip = 224;
    private const int HeaderHeightDip = 92;
    private const int NavTopDip = 92;
    private const int PagePadDip = 28;

    // Segoe Fluent Icons / MDL2 Assets code points.
    private const string GlyphHome = "";
    private const string GlyphChat = "";
    private const string GlyphGlobe = "";
    private const string GlyphKeyboard = "";
    private const string GlyphCommand = "";
    private const string GlyphSettings = "";

    private static int _lastPage;

    private readonly IAppWindowHost _host;
    private readonly Panel _sidebar;
    private readonly Panel _header;
    private readonly Panel _scrollHost;
    private readonly HudCaptionButton _min;
    private readonly HudCaptionButton _close;
    private readonly List<Page> _pages = [];
    private readonly Dictionary<Control, int> _rowHeights = [];
    private readonly HudCard _statusCard;
    private readonly HudButton _loginButton;
    private readonly HudSegmented _modelSeg;
    private readonly HudSegmented _modeSeg;
    private readonly HudHotkeyRow _dictationKey;
    private readonly HudToggleRow _cleanToggle;
    private readonly HudSegmented _cleanSeg;
    private readonly HudHotkeyRow _cleanKey;
    private readonly HudButton _cleanWindowButton;
    private readonly HudSegmented _askSeg;
    private readonly HudHotkeyRow _askModelKey;
    private readonly HudToggleRow _askPopupToggle;
    private readonly HudButton _askWindowButton;
    private readonly HudHotkeyRow _translateKey;
    private readonly ComboBox _translateLanguage;
    private readonly HudSettingRow _openAiRow;
    private readonly HudButton _openAiButton;
    private readonly HudHotkeyRow _chatgptKey;
    private readonly HudHotkeyRow _geminiKey;
    private readonly HudHotkeyRow _claudeKey;
    private readonly HudHotkeyRow _enterKey;
    private readonly HudHotkeyRow _windowKey;
    private readonly HudCommandList _commands;
    private readonly HudToggleRow _muteToggle;
    private readonly HudToggleRow _enterToggle;
    private readonly HudToggleRow _clipboardToggle;
    private readonly HudToggleRow _startupToggle;
    private readonly HudSegmented _languageSeg;
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
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        MinimumSize = new Size(760, 520);
        Size = new Size(920, 680);
        BackColor = HudTheme.Window;
        ForeColor = HudTheme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = HudTheme.BodyText;

        _sidebar = new Panel {
            Dock = Loc.Rtl ? DockStyle.Right : DockStyle.Left,
            Width = SidebarWidthDip,
            BackColor = HudTheme.Sidebar,
            TabIndex = 0,
        };
        _sidebar.Paint += OnSidebarPaint;
        HookDrag(_sidebar);

        _header = new Panel {
            Dock = DockStyle.Top,
            Height = HeaderHeightDip,
            BackColor = HudTheme.Window,
            TabIndex = 2,
        };
        _header.Paint += OnHeaderPaint;
        HookDrag(_header);

        _close = new HudCaptionButton { Icon = HudCaptionButton.Glyph.Close };
        _min = new HudCaptionButton { Icon = HudCaptionButton.Glyph.Minimize };
        _close.Click += (_, _) => Close();
        _min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _header.Controls.Add(_close);
        _header.Controls.Add(_min);

        _scrollHost = new Panel {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = HudTheme.Window,
            TabIndex = 1,
        };
        _scrollHost.ClientSizeChanged += (_, _) => Relayout();

        // ── Home ──────────────────────────────────────────────
        var home = AddPage(GlyphHome, Loc.T("Home"), Loc.T("Status, model and how you dictate."));
        _statusCard = AddCard(home);
        _statusCard.Dividers = false;
        _statusCard.Paint += OnStatusCardPaint;
        _loginButton = MakeButton(Loc.T("Open window"), HudButton.Kind.Primary);
        _loginButton.Click += (_, _) => _host.ToggleLoginWindow();
        _statusCard.Controls.Add(_loginButton);

        AddSection(home, Loc.T("Dictation"));
        var dictation = AddCard(home);
        _modelSeg = ModelSegmented(provider => _host.SelectModel(provider));
        AddRow(dictation, Loc.T("Transcription model"), Loc.T("Turns your speech into text."), _modelSeg, 300);
        _modeSeg = new HudSegmented { Items = [Loc.T("Hold to talk"), Loc.T("Press to start/stop")] };
        _modeSeg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                _host.SetToggleRecording(_modeSeg.SelectedIndex == 1);
            }
        };
        AddRow(dictation, Loc.T("Recording mode"), null, _modeSeg, 300);
        _dictationKey = AddHotkey(dictation, Loc.T("Dictation key"), AppShortcut.Dictation, optional: false);

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
        var translate = AddPage(GlyphGlobe, Loc.T("Translate"), Loc.T("Speak in one language, paste in another."));
        var translateCard = AddCard(translate);
        _translateKey = AddHotkey(translateCard, Loc.T("Translate shortcut"), AppShortcut.Translate);
        _translateKey.MultiKey = true;
        _translateLanguage = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Standard,
            MaxDropDownItems = 12,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 28,
            BackColor = HudTheme.Track,
            ForeColor = HudTheme.Body,
            Font = HudTheme.BodyText,
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        _translateLanguage.DrawItem += OnLanguageDrawItem;
        foreach (var (code, name) in GoogleTranslation.Languages) {
            _translateLanguage.Items.Add(new LanguageItem(code, name));
        }
        _translateLanguage.SelectedIndexChanged += (_, _) => {
            if (!_syncing && _translateLanguage.SelectedItem is LanguageItem item) {
                _host.SetTranslateLanguage(item.Code);
            }
        };
        AddRow(translateCard, Loc.T("Translate to"), null, _translateLanguage, 220, heightDip: 0);
        _openAiButton = MakeButton(Loc.T("Add key…"), HudButton.Kind.Ghost);
        _openAiButton.Click += (_, _) => _host.EditOpenAiKey();
        _openAiRow = AddRow(translateCard, Loc.T("OpenAI API key"), null, _openAiButton, 200);

        // ── Shortcuts ─────────────────────────────────────────
        var shortcuts = AddPage(GlyphKeyboard, Loc.T("Shortcuts"), Loc.T("Keys that switch models and open windows."));
        AddSection(shortcuts, Loc.T("Switch model"));
        var switchCard = AddCard(shortcuts);
        _chatgptKey = AddHotkey(switchCard, Loc.T("Switch to ChatGPT"), AppShortcut.ChatGpt);
        _geminiKey = AddHotkey(switchCard, Loc.T("Switch to Gemini"), AppShortcut.Gemini);
        _claudeKey = AddHotkey(switchCard, Loc.T("Switch to Claude (live transcript)"), AppShortcut.Claude);
        AddSection(shortcuts, Loc.T("Other shortcuts"));
        var otherKeys = AddCard(shortcuts);
        _windowKey = AddHotkey(otherKeys, Loc.T("Open model window"), AppShortcut.ModelWindow);
        _enterKey = AddHotkey(otherKeys, Loc.T("Auto Enter shortcut"), AppShortcut.PressEnter);

        // ── Commands ──────────────────────────────────────────
        var commands = AddPage(GlyphCommand, Loc.T("Commands"), Loc.T("Custom commands, workspace modes and word replacements."));
        AddSection(commands, Loc.T("Custom commands"));
        var commandsCard = AddCard(commands);
        var editCommands = MakeButton(Loc.T("Add or edit…"), HudButton.Kind.Ghost);
        editCommands.Click += (_, _) => _host.OpenCommands();
        AddRow(commandsCard, Loc.T("Custom commands"), Loc.T("Your own prompts, run on what you dictate."), editCommands, 200);
        _commands = new HudCommandList { BackColor = HudTheme.Card };
        commandsCard.Controls.Add(_commands);

        AddSection(commands, Loc.T("Voice & text"));
        var voiceCard = AddCard(commands);
        var modesButton = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        modesButton.Click += (_, _) => _host.EditWorkspaceModes();
        AddRow(voiceCard, Loc.T("Workspace modes"), Loc.T("Say a word to press a shortcut."), modesButton, 200);
        var wordsButton = MakeButton(Loc.T("Edit…"), HudButton.Kind.Ghost);
        wordsButton.Click += (_, _) => _host.EditWordReplacements();
        AddRow(voiceCard, Loc.T("Word replacements"), Loc.T("Fix dialect words in every transcript."), wordsButton, 200);

        // ── Settings ──────────────────────────────────────────
        var settings = AddPage(GlyphSettings, Loc.T("Settings"), Loc.T("General options, language and app controls."));
        AddSection(settings, Loc.T("General"));
        var general = AddCard(settings);
        _muteToggle = AddToggle(general, Loc.T("Pause or mute other apps while dictating"), null,
            toggle => _host.SetMuteOthers(toggle.Checked));
        _enterToggle = AddToggle(general, Loc.T("Press Enter after paste"), null,
            toggle => _host.SetPressEnterAfterPaste(toggle.Checked));
        _clipboardToggle = AddToggle(general, Loc.T("Keep transcript on clipboard"), null,
            toggle => _host.SetKeepTranscriptOnClipboard(toggle.Checked));
        _startupToggle = AddToggle(general, Loc.T("Start EchoType with Windows"), null,
            toggle => _host.SetStartWithWindows(toggle.Checked));

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
        var openLog = MakeButton(Loc.T("Open log"), HudButton.Kind.Ghost);
        openLog.Click += (_, _) => _host.OpenLog();
        AddRow(about, Loc.T("Diagnostics log"), Loc.T("Open the log when something goes wrong."), openLog, 160);
        var quit = MakeButton(Loc.T("Quit"), HudButton.Kind.Danger);
        quit.Click += (_, _) => _host.Quit();
        AddRow(about, Loc.T("Quit EchoType"), Loc.T("Stops dictation and removes the tray icon."), quit, 160);

        // Dock order: the sidebar takes the full height, the header tops the rest.
        Controls.Add(_scrollHost);
        Controls.Add(_header);
        Controls.Add(_sidebar);

        _tick = new System.Windows.Forms.Timer { Interval = 16 };
        _tick.Tick += (_, _) => OnTick();
        _intro = new System.Windows.Forms.Timer { Interval = 16 };
        _intro.Tick += (_, _) => StepIntro();

        host.UiChanged += OnHostChanged;
        Load += (_, _) => {
            HudTheme.ApplyChrome(this);
            ApplyChromeMetrics();
            ShowPage(_lastPage);
            SyncFromHost();
            Opacity = 0;
            _intro.Start();
            _tick.Start();
        };
        Resize += (_, _) => {
            PlaceCaptionButtons();
            Relayout();
            _header.Invalidate();
        };
        Deactivate += (_, _) => StopCapture();
    }

    protected override CreateParams CreateParams {
        get {
            var cp = base.CreateParams;
            cp.Style |= NativeMethods.WS_THICKFRAME;
            cp.ClassStyle |= NativeMethods.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) {
        base.OnDpiChanged(e);
        ApplyChromeMetrics();
        Relayout();
    }

    protected override void OnShown(EventArgs e) {
        base.OnShown(e);
        HudTheme.ApplyChrome(this);
        Relayout();
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        HudTheme.ApplyChrome(this);
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
        } else if (e.Control && e.KeyCode == Keys.Tab && _capturing == null) {
            // Ctrl+Tab / Ctrl+Shift+Tab step through the pages.
            ShowPage((_page + (e.Shift ? _pages.Count - 1 : 1)) % _pages.Count);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == NativeMethods.WM_NCCALCSIZE && m.WParam != IntPtr.Zero) {
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == NativeMethods.WM_NCHITTEST) {
            int x = (short)(m.LParam.ToInt32() & 0xFFFF);
            int y = (short)((m.LParam.ToInt32() >> 16) & 0xFFFF);
            var pt = PointToClient(new Point(x, y));
            int grip = HudTheme.Dip(this, 8);
            bool left = pt.X <= grip;
            bool right = pt.X >= ClientSize.Width - grip;
            bool top = pt.Y <= grip;
            bool bottom = pt.Y >= ClientSize.Height - grip;
            if (top && left) { m.Result = (IntPtr)NativeMethods.HTTOPLEFT; return; }
            if (top && right) { m.Result = (IntPtr)NativeMethods.HTTOPRIGHT; return; }
            if (bottom && left) { m.Result = (IntPtr)NativeMethods.HTBOTTOMLEFT; return; }
            if (bottom && right) { m.Result = (IntPtr)NativeMethods.HTBOTTOMRIGHT; return; }
            if (left) { m.Result = (IntPtr)NativeMethods.HTLEFT; return; }
            if (right) { m.Result = (IntPtr)NativeMethods.HTRIGHT; return; }
            if (top) { m.Result = (IntPtr)NativeMethods.HTTOP; return; }
            if (bottom) { m.Result = (IntPtr)NativeMethods.HTBOTTOM; return; }
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            _host.UiChanged -= OnHostChanged;
            _host.Hotkey.KeyCaptured -= OnKeyCaptured;
            _host.Hotkey.ChordCaptured -= OnChordCaptured;
            _host.Hotkey.ChordReleased -= OnChordReleased;
            _tick.Dispose();
            _intro.Dispose();
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
            _modelSeg.SetSilent(ProviderIndex(s.TranscriptionProvider));
            _cleanToggle.SetSilent(s.AutoClean);
            _cleanSeg.SetSilent(ProviderIndex(s.CleanProvider));
            _cleanWindowButton.Text = Loc.F(_host.CleanWindowVisible ? "Close {0} window" : "Open {0} window", _host.CleanModelName);
            _askSeg.SetSilent(ProviderIndex(s.AskProvider));
            _askWindowButton.Text = Loc.F(_host.AskWindowVisible ? "Close {0} window" : "Open {0} window", _host.AskModelName);
            _modeSeg.SetSilent(s.ToggleRecording ? 1 : 0);
            _muteToggle.SetSilent(s.MuteOtherAppsWhileDictating);
            _enterToggle.SetSilent(s.PressEnterAfterPaste);
            _clipboardToggle.SetSilent(s.KeepTranscriptOnClipboard);
            _startupToggle.SetSilent(s.StartWithWindows);
            _askPopupToggle.SetSilent(s.ShowAskReplyWindow);
            _languageSeg.SetSilent(Loc.Arabic ? 1 : 0);
            _dictationKey.Value = HotkeyNames.For(s.HotkeyVk);
            _askModelKey.Value = HotkeyLabel(s.AskModelVk);
            if (_capturing != _translateKey) {
                _translateKey.Value = HotkeyNames.For(s.TranslateChord);
            }
            _translateLanguage.SelectedIndex = IndexOfLanguage(s.TranslateTargetLanguage);
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
            foreach (var row in new[] { _dictationKey, _askModelKey, _translateKey, _chatgptKey, _geminiKey, _claudeKey, _enterKey, _cleanKey, _windowKey }) {
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
            _statusCard.Accent = HudTheme.PhaseAccent(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
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
        e.Graphics.Clear(HudTheme.Sidebar);
        int w = _sidebar.Width;

        // Brand: a status dot that glows and pulses while listening, the name, the phase.
        Color accent = HudTheme.PhaseAccent(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        float pulse = _host.Phase == AppPhase.Listening
            ? 0.55f + 0.45f * MathF.Sin(_time * 5.5f)
            : 1f;
        float glow = HudTheme.DipF(this, 7)
            + HudTheme.DipF(this, 3) * (pulse - 0.55f)
            + HudTheme.DipF(this, 4) * _smoothed * pulse;
        float cx = HudTheme.DipF(this, 34);
        float cy = HudTheme.DipF(this, 40);
        if (Loc.Rtl) {
            cx = w - cx;
        }
        using (var glowBrush = new SolidBrush(Color.FromArgb((int)(36 + 40 * _smoothed), accent))) {
            e.Graphics.FillEllipse(glowBrush, cx - glow, cy - glow, glow * 2, glow * 2);
        }
        using (var dot = new SolidBrush(accent)) {
            float d = HudTheme.DipF(this, 4.5f);
            e.Graphics.FillEllipse(dot, cx - d, cy - d, d * 2, d * 2);
        }
        int textLeft = HudTheme.Dip(this, 54);
        int textWidth = Math.Max(0, w - textLeft - HudTheme.Dip(this, 16));
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, "EchoType", HudTheme.Caption,
            Loc.Mirror(new Rectangle(textLeft, HudTheme.Dip(this, 22), textWidth, HudTheme.Dip(this, 24)), w),
            HudTheme.Body, Loc.Flags(flags));
        TextRenderer.DrawText(e.Graphics, HudTheme.PhaseLabel(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady),
            HudTheme.Tiny,
            Loc.Mirror(new Rectangle(textLeft, HudTheme.Dip(this, 46), textWidth, HudTheme.Dip(this, 18)), w),
            accent, Loc.Flags(flags));

        TextRenderer.DrawText(e.Graphics, Loc.F("Version {0}", _version), HudTheme.Tiny,
            Loc.Mirror(new Rectangle(HudTheme.Dip(this, 26), _sidebar.Height - HudTheme.Dip(this, 36), textWidth, HudTheme.Dip(this, 18)), w),
            HudTheme.Muted, Loc.Flags(flags));

        // Hairline between the sidebar and the page.
        using var line = new SolidBrush(HudTheme.Divider);
        e.Graphics.FillRectangle(line, Loc.Rtl ? 0 : w - 1, 0, 1, _sidebar.Height);
    }

    private void OnHeaderPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Window);
        if (_pages.Count == 0) {
            return;
        }
        var page = _pages[_page];
        int w = _header.Width;
        int left = HudTheme.Dip(this, PagePadDip + 4);
        int buttonsEdge = Loc.Rtl ? w - _min.Right : _min.Left;
        int right = Math.Max(left + 40, buttonsEdge - HudTheme.Dip(this, 12));
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, page.Title, HudTheme.Display,
            Loc.Mirror(new Rectangle(left, HudTheme.Dip(this, 26), right - left, HudTheme.Dip(this, 32)), w),
            HudTheme.Body, Loc.Flags(flags));
        // The subtitle may run under the caption buttons; they sit higher.
        TextRenderer.DrawText(e.Graphics, page.Subtitle, HudTheme.Small,
            Loc.Mirror(new Rectangle(left, HudTheme.Dip(this, 58), w - left * 2, HudTheme.Dip(this, 20)), w),
            HudTheme.Muted, Loc.Flags(flags));
    }

    private void OnStatusCardPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        int cw = _statusCard.Width;
        int left = HudTheme.Dip(this, 30);
        int trailing = cw - _loginButton.Width - HudTheme.Dip(this, 22 + 20);
        bool listening = _host.Phase == AppPhase.Listening;
        int waveWidth = HudTheme.Dip(this, 120);
        int textWidth = Math.Max(0, trailing - left - (listening ? waveWidth + HudTheme.Dip(this, 16) : 0));

        string phase = HudTheme.PhaseLabel(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        string body = !_host.Online ? Loc.T("No internet")
            : !_host.LoggedIn ? _host.ModelName + " · " + Loc.T("Sign in to dictate")
            : _host.StatusText;
        int titleH = HudTheme.Dip(this, 30);
        int bodyH = HudTheme.Dip(this, 20);
        int top = (_statusCard.Height - titleH - bodyH) / 2;
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, phase, HudTheme.Display,
            Loc.Mirror(new Rectangle(left, top, textWidth, titleH), cw),
            HudTheme.Body, Loc.Flags(flags));
        TextRenderer.DrawText(e.Graphics, body, HudTheme.Small,
            Loc.Mirror(new Rectangle(left, top + titleH, textWidth, bodyH), cw),
            HudTheme.Muted, Loc.Flags(flags));

        if (listening) {
            int waveH = HudTheme.Dip(this, 36);
            var wave = Loc.Mirror(new Rectangle(trailing - waveWidth, (_statusCard.Height - waveH) / 2, waveWidth, waveH), cw);
            HudTheme.DrawWave(e.Graphics, wave, _bars, HudTheme.Teal, HudTheme.DipF(this, 18));
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
        PlaceCaptionButtons();
    }

    private void PlaceCaptionButtons() {
        int pad = HudTheme.Dip(this, 12);
        _close.Size = new Size(HudTheme.Dip(this, 36), HudTheme.Dip(this, 28));
        _min.Size = _close.Size;
        int top = HudTheme.Dip(this, 12);
        if (Loc.Rtl) {
            _close.Location = new Point(pad, top);
            _min.Location = new Point(_close.Right + HudTheme.Dip(this, 4), top);
            return;
        }
        _close.Location = new Point(_header.Width - _close.Width - pad, top);
        _min.Location = new Point(_close.Left - _min.Width - HudTheme.Dip(this, 4), top);
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
                y += card.Height + HudTheme.Dip(this, 4);
                card.Invalidate();
            }
            first = false;
        }
        return y + HudTheme.Dip(this, 24);
    }

    private void LayoutStatusCard() {
        _statusCard.Height = HudTheme.Dip(this, 104);
        int w = Math.Min(HudTheme.Dip(this, 220), _statusCard.Width / 2);
        int h = HudTheme.Dip(this, 38);
        var r = new Rectangle(_statusCard.Width - w - HudTheme.Dip(this, 22), (_statusCard.Height - h) / 2, w, h);
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
            int h = _rowHeights.TryGetValue(child, out int dip) ? HudTheme.Dip(this, dip) : child.Height;
            if (child == _commands) {
                // Breathing room above and below the list.
                h += HudTheme.Dip(this, 12);
            }
            child.SetBounds(padX, y, inner, h);
            y += h + gap;
        }
        card.Height = y - gap + padY;
    }

    // ── Building blocks ───────────────────────────────────────

    private Page AddPage(string glyph, string title, string subtitle) {
        var nav = new HudNavItem { Glyph = glyph, Text = title };
        int index = _pages.Count;
        nav.Click += (_, _) => ShowPage(index);
        _sidebar.Controls.Add(nav);
        var body = new Panel { BackColor = HudTheme.Window, Visible = false };
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

    private static HudCard AddCard(Page page) {
        var card = new HudCard();
        page.Body.Controls.Add(card);
        page.Blocks.Add(card);
        return card;
    }

    private void AddRowControl(HudCard card, Control control, int heightDip) {
        control.BackColor = HudTheme.Card;
        _rowHeights[control] = heightDip;
        card.Controls.Add(control);
    }

    private HudSettingRow AddRow(HudCard card, string title, string? description, Control trailing, int widthDip, int heightDip = 36) {
        var row = new HudSettingRow { Title = title, Description = description };
        if (trailing is HudButton or HudSegmented) {
            trailing.BackColor = HudTheme.Card;
        }
        row.SetTrailing(trailing, widthDip, heightDip);
        AddRowControl(card, row, description == null ? 56 : 62);
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
        if (shortcut == AppShortcut.Translate) {
            // Translate takes one to three keys (e.g. Ctrl+Shift+T); it is saved
            // once every key of the combination has been released.
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
    }

    private void OnChordCaptured(HotkeyChord chord) {
        if (IsDisposed || _capturing != _translateKey || chord.IsEmpty) {
            return;
        }
        _translateKey.CapturePreview = HotkeyNames.For(chord);
        _translateKey.Invalidate();
    }

    private void OnChordReleased(HotkeyChord chord) {
        if (IsDisposed || _capturing != _translateKey) {
            return;
        }
        StopCapture();
        if (chord.IsEmpty) {
            return;
        }
        _host.TrySetTranslateChord(chord);
        _translateKey.Value = HotkeyNames.For(_host.Settings.TranslateChord);
        _translateKey.Invalidate();
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

    private void OnLanguageDrawItem(object? sender, DrawItemEventArgs e) {
        if (e.Index < 0) {
            return;
        }
        // The closed box and the hovered list item get a soft highlight; the rest stay dark.
        bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var fill = new SolidBrush(hot ? Color.FromArgb(255, 34, 52, 54) : HudTheme.Track)) {
            e.Graphics.FillRectangle(fill, e.Bounds);
        }
        var text = Rectangle.Inflate(e.Bounds, -HudTheme.Dip(this, 8), 0);
        TextRenderer.DrawText(e.Graphics, _translateLanguage.Items[e.Index]?.ToString(),_translateLanguage.Font, text,
            hot ? HudTheme.Teal : HudTheme.Body,
            Loc.Flags(TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix));
    }

    private int IndexOfLanguage(string code) {
        for (int i = 0; i < _translateLanguage.Items.Count; i++) {
            if (_translateLanguage.Items[i] is LanguageItem item
                && string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)) {
                return i;
            }
        }
        return -1;
    }

    private sealed record LanguageItem(string Code, string Name) {
        public override string ToString() => Name;
    }

    private static string HotkeyLabel(int vk) => vk <= 0 ? Loc.T("(none)") : HotkeyNames.For(vk);
}
