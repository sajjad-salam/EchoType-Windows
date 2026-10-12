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
/// Main EchoType window — the same dark pill language as the HUD, with every
/// setting that used to live only in the tray.
/// </summary>
internal sealed class AppWindow : Form {

    private readonly IAppWindowHost _host;
    private readonly Panel _caption;
    private readonly Panel _scrollHost;
    private readonly FlowLayoutPanel _flow;
    private readonly HudCaptionButton _min;
    private readonly HudCaptionButton _close;
    private readonly HudCard _statusCard;
    private readonly HudSegmented _modelSeg;
    private readonly HudSegmented _modeSeg;
    private readonly HudButton _loginButton;
    private readonly HudHotkeyRow _dictationKey;
    private readonly HudHotkeyRow _askModelKey;
    private readonly HudHotkeyRow _translateKey;
    private readonly ComboBox _translateLanguage;
    private readonly HudHotkeyRow _chatgptKey;
    private readonly HudHotkeyRow _geminiKey;
    private readonly HudHotkeyRow _claudeKey;
    private readonly HudHotkeyRow _enterKey;
    private readonly HudHotkeyRow _windowKey;
    private readonly HudToggleRow _muteToggle;
    private readonly HudToggleRow _cleanToggle;
    private readonly HudSegmented _cleanSeg;
    private readonly HudButton _cleanWindowButton;
    private readonly HudButton _cleanPromptButton;
    private readonly HudHotkeyRow _cleanKey;
    private readonly HudSegmented _askSeg;
    private readonly HudButton _askWindowButton;
    private readonly HudToggleRow _enterToggle;
    private readonly HudToggleRow _clipboardToggle;
    private readonly HudToggleRow _startupToggle;
    private readonly HudToggleRow _askPopupToggle;
    private readonly HudSegmented _languageSeg;
    private readonly float[] _spectrum = new float[24];
    private readonly HudCommandList _commands;
    private readonly HudButton _editCommands;
    private readonly HudButton _openLog;
    private readonly HudButton _quit;
    private readonly System.Windows.Forms.Timer _tick;
    private readonly System.Windows.Forms.Timer _intro;
    private readonly float[] _bars = new float[24];

    private HudHotkeyRow? _capturing;
    private float _time;
    private float _smoothed;
    private float _introProgress;
    private bool _syncing;
    private bool _trayHintShown;
    private int _dragX;
    private int _dragY;
    private bool _dragging;

    public AppWindow(IAppWindowHost host) {
        _host = host;

        Text = "EchoType";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        MinimumSize = new Size(480, 560);
        Size = new Size(540, 760);
        BackColor = HudTheme.Window;
        ForeColor = HudTheme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        Font = HudTheme.BodyText;

        _caption = new Panel {
            Dock = DockStyle.Top,
            Height = HudTheme.CaptionHeightDip,
            BackColor = HudTheme.Window,
        };
        _caption.Paint += OnCaptionPaint;
        _caption.MouseDown += OnCaptionMouseDown;
        _caption.MouseMove += OnCaptionMouseMove;
        _caption.MouseUp += (_, _) => _dragging = false;

        _close = new HudCaptionButton { Icon = HudCaptionButton.Glyph.Close, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _min = new HudCaptionButton { Icon = HudCaptionButton.Glyph.Minimize, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        _close.Click += (_, _) => Close();
        _min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _caption.Controls.Add(_close);
        _caption.Controls.Add(_min);

        _flow = new FlowLayoutPanel {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = HudTheme.Window,
            Padding = new Padding(10, 6, 10, 18),
            Location = Point.Empty,
        };

        _scrollHost = new Panel {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = HudTheme.Window,
        };
        _scrollHost.Controls.Add(_flow);
        _scrollHost.Resize += (_, _) => Relayout();

        _statusCard = MakeCard();
        _statusCard.Height = 168;
        _statusCard.Paint += OnStatusCardPaint;

        _loginButton = MakeButton(Loc.T("Open window"), HudButton.Kind.Primary);
        _loginButton.Click += (_, _) => _host.ToggleLoginWindow();
        PlaceOnCard(_statusCard, _loginButton, 18);

        var modelCard = MakeCard();
        AddCaption(modelCard, Loc.T("Transcription model"));
        _modelSeg = new HudSegmented { Items = ["ChatGPT", "Gemini", "Claude"] };
        _modelSeg.SelectedIndexChanged += (_, _) => {
            if (_syncing) {
                return;
            }
            _host.SelectModel(_modelSeg.SelectedIndex switch {
                1 => TranscriptionProvider.Gemini,
                2 => TranscriptionProvider.Claude,
                _ => TranscriptionProvider.ChatGpt,
            });
        };
        AddControl(modelCard, _modelSeg, 40);
        _chatgptKey = AddHotkey(modelCard, Loc.T("Switch to ChatGPT"), AppShortcut.ChatGpt);
        _geminiKey = AddHotkey(modelCard, Loc.T("Switch to Gemini"), AppShortcut.Gemini);
        _claudeKey = AddHotkey(modelCard, Loc.T("Switch to Claude (live transcript)"), AppShortcut.Claude);
        FinishCard(modelCard);

        var cleanCard = MakeCard();
        AddCaption(cleanCard, Loc.T("Auto Clean (cleaning model)"));
        _cleanToggle = AddToggle(cleanCard, Loc.T("Auto Clean dictation"),
            toggle => _host.SetAutoClean(toggle.Checked));
        _cleanKey = AddHotkey(cleanCard, Loc.T("Auto Clean on/off shortcut"), AppShortcut.AutoClean);
        _cleanSeg = new HudSegmented { Items = ["ChatGPT", "Gemini", "Claude"] };
        _cleanSeg.SelectedIndexChanged += (_, _) => {
            if (_syncing) {
                return;
            }
            _host.SetCleanModel(_cleanSeg.SelectedIndex switch {
                1 => TranscriptionProvider.Gemini,
                2 => TranscriptionProvider.Claude,
                _ => TranscriptionProvider.ChatGpt,
            });
        };
        AddControl(cleanCard, _cleanSeg, 40);
        _cleanWindowButton = MakeButton(Loc.T("Open cleaning window"), HudButton.Kind.Ghost);
        _cleanWindowButton.Click += (_, _) => _host.ToggleCleanWindow();
        AddControl(cleanCard, _cleanWindowButton, 44);
        _cleanPromptButton = MakeButton(Loc.T("Edit cleaning prompt…"), HudButton.Kind.Ghost);
        _cleanPromptButton.Click += (_, _) => _host.EditCleanPrompt();
        AddControl(cleanCard, _cleanPromptButton, 44);
        FinishCard(cleanCard);

        var askCard = MakeCard();
        AddCaption(askCard, Loc.T("Ask model (answers the Ask shortcut)"));
        _askSeg = new HudSegmented { Items = ["ChatGPT", "Gemini", "Claude"] };
        _askSeg.SelectedIndexChanged += (_, _) => {
            if (_syncing) {
                return;
            }
            _host.SetAskModel(_askSeg.SelectedIndex switch {
                1 => TranscriptionProvider.Gemini,
                2 => TranscriptionProvider.Claude,
                _ => TranscriptionProvider.ChatGpt,
            });
        };
        AddControl(askCard, _askSeg, 40);
        _askWindowButton = MakeButton(Loc.T("Open Ask model window…"), HudButton.Kind.Ghost);
        _askWindowButton.Click += (_, _) => _host.ToggleAskWindow();
        AddControl(askCard, _askWindowButton, 44);
        FinishCard(askCard);

        var wordsCard = MakeCard();
        AddCaption(wordsCard, Loc.T("Word replacements (dialect)"));
        var wordsButton = MakeButton(Loc.T("Edit word replacements…"), HudButton.Kind.Ghost);
        wordsButton.Click += (_, _) => _host.EditWordReplacements();
        AddControl(wordsCard, wordsButton, 44);
        FinishCard(wordsCard);

        var modesCard = MakeCard();
        AddCaption(modesCard, Loc.T("Workspace modes (voice → shortcut)"));
        var modesButton = MakeButton(Loc.T("Edit workspace modes…"), HudButton.Kind.Ghost);
        modesButton.Click += (_, _) => _host.EditWorkspaceModes();
        AddControl(modesCard, modesButton, 44);
        FinishCard(modesCard);

        var recordCard = MakeCard();
        AddCaption(recordCard, Loc.T("Recording"));
        _modeSeg = new HudSegmented { Items = [Loc.T("Hold to talk"), Loc.T("Press to start/stop")] };
        _modeSeg.SelectedIndexChanged += (_, _) => {
            if (_syncing) {
                return;
            }
            _host.SetToggleRecording(_modeSeg.SelectedIndex == 1);
        };
        AddControl(recordCard, _modeSeg, 40);
        _dictationKey = AddHotkey(recordCard, Loc.T("Dictation key"), AppShortcut.Dictation, optional: false);
        _askModelKey = AddHotkey(recordCard, Loc.T("Ask model"), AppShortcut.AskModel);
        _translateKey = AddHotkey(recordCard, Loc.T("Translate"), AppShortcut.Translate);
        _translateKey.MultiKey = true;
        _translateLanguage = new ComboBox {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            ForeColor = HudTheme.Body,
            Font = Font,
            RightToLeft = Loc.Rtl ? RightToLeft.Yes : RightToLeft.No,
        };
        foreach (var (code, name) in GoogleTranslation.Languages) {
            _translateLanguage.Items.Add(new LanguageItem(code, name));
        }
        _translateLanguage.SelectedIndexChanged += (_, _) => {
            if (!_syncing && _translateLanguage.SelectedItem is LanguageItem item) {
                _host.SetTranslateLanguage(item.Code);
            }
        };
        AddControl(recordCard, _translateLanguage, 32);
        var openAiKey = MakeButton(Loc.T("OpenAI API key…"), HudButton.Kind.Ghost);
        openAiKey.Click += (_, _) => _host.EditOpenAiKey();
        AddControl(recordCard, openAiKey, 44);
        FinishCard(recordCard);

        var optionsCard = MakeCard();
        AddCaption(optionsCard, Loc.T("Options"));
        _muteToggle = AddToggle(optionsCard, Loc.T("Pause or mute other apps while dictating"),
            toggle => _host.SetMuteOthers(toggle.Checked));
        _enterToggle = AddToggle(optionsCard, Loc.T("Press Enter after paste"),
            toggle => _host.SetPressEnterAfterPaste(toggle.Checked));
        _enterKey = AddHotkey(optionsCard, Loc.T("Auto Enter shortcut"), AppShortcut.PressEnter);
        _clipboardToggle = AddToggle(optionsCard, Loc.T("Keep transcript on clipboard"),
            toggle => _host.SetKeepTranscriptOnClipboard(toggle.Checked));
        _askPopupToggle = AddToggle(optionsCard, Loc.T("Show Ask model replies in a popup"),
            toggle => _host.SetShowAskReplyWindow(toggle.Checked));
        _startupToggle = AddToggle(optionsCard, Loc.T("Start EchoType with Windows"),
            toggle => _host.SetStartWithWindows(toggle.Checked));
        FinishCard(optionsCard);

        var languageCard = MakeCard();
        AddCaption(languageCard, Loc.T("Language") + (Loc.Arabic ? "" : " · اللغة"));
        // Each name in its own language so it can be found whatever the current UI is.
        _languageSeg = new HudSegmented { Items = ["English", "العربية"] };
        _languageSeg.SelectedIndexChanged += (_, _) => {
            if (!_syncing) {
                _host.SetUiLanguage(_languageSeg.SelectedIndex == 1 ? Loc.ArabicCode : Loc.English);
            }
        };
        AddControl(languageCard, _languageSeg, 40);
        FinishCard(languageCard);

        var commandsCard = MakeCard();
        AddCaption(commandsCard, Loc.T("Custom commands"));
        _commands = new HudCommandList();
        AddControl(commandsCard, _commands, 48);
        _editCommands = MakeButton(Loc.T("Add or edit…"), HudButton.Kind.Ghost);
        _editCommands.Click += (_, _) => _host.OpenCommands();
        AddControl(commandsCard, _editCommands, 38);
        FinishCard(commandsCard);

        var moreCard = MakeCard();
        AddCaption(moreCard, Loc.T("Model window"));
        _windowKey = AddHotkey(moreCard, Loc.T("Open model window"), AppShortcut.ModelWindow);
        var actions = new FlowLayoutPanel {
            // Full card width for Arabic so the buttons can line up from the right.
            AutoSize = !Loc.Rtl,
            WrapContents = false,
            BackColor = HudTheme.Card,
            Margin = new Padding(0, 8, 0, 0),
            FlowDirection = Loc.Rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        _openLog = MakeButton(Loc.T("Open log"), HudButton.Kind.Ghost);
        _openLog.Width = 120;
        _openLog.Click += (_, _) => _host.OpenLog();
        _quit = MakeButton(Loc.T("Quit EchoType"), HudButton.Kind.Danger);
        _quit.Width = 140;
        _quit.Click += (_, _) => _host.Quit();
        actions.Controls.Add(_openLog);
        actions.Controls.Add(_quit);
        AddControl(moreCard, actions, 46);
        FinishCard(moreCard);

        Controls.Add(_scrollHost);
        Controls.Add(_caption);

        _tick = new System.Windows.Forms.Timer { Interval = 16 };
        _tick.Tick += (_, _) => OnTick();
        _intro = new System.Windows.Forms.Timer { Interval = 16 };
        _intro.Tick += (_, _) => StepIntro();

        host.UiChanged += OnHostChanged;
        Load += (_, _) => {
            HudTheme.ApplyChrome(this);
            _caption.Height = HudTheme.Dip(this, HudTheme.CaptionHeightDip);
            PlaceCaptionButtons();
            SyncFromHost();
            Opacity = 0;
            _intro.Start();
            _tick.Start();
        };
        Resize += (_, _) => {
            PlaceCaptionButtons();
            Relayout();
            _caption.Invalidate();
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
        _caption.Height = HudTheme.Dip(this, HudTheme.CaptionHeightDip);
        PlaceCaptionButtons();
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
            _modelSeg.SetSilent(s.TranscriptionProvider switch {
                TranscriptionProvider.Gemini => 1,
                TranscriptionProvider.Claude => 2,
                _ => 0,
            });
            _cleanToggle.SetSilent(s.AutoClean);
            _cleanSeg.SetSilent(s.CleanProvider switch {
                TranscriptionProvider.Gemini => 1,
                TranscriptionProvider.Claude => 2,
                _ => 0,
            });
            _cleanWindowButton.Text = Loc.F(_host.CleanWindowVisible ? "Close {0} window" : "Open {0} window", _host.CleanModelName);
            _askSeg.SetSilent(s.AskProvider switch {
                TranscriptionProvider.Gemini => 1,
                TranscriptionProvider.Claude => 2,
                _ => 0,
            });
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
            _chatgptKey.Value = HotkeyLabel(s.ChatGptSwitchVk);
            _geminiKey.Value = HotkeyLabel(s.GeminiSwitchVk);
            _claudeKey.Value = HotkeyLabel(s.ClaudeSwitchVk);
            _enterKey.Value = HotkeyLabel(s.PressEnterToggleVk);
            _cleanKey.Value = HotkeyLabel(s.AutoCleanToggleVk);
            _windowKey.Value = HotkeyLabel(s.OpenModelWindowVk);
            _dictationKey.Invalidate();
            _askModelKey.Invalidate();
            _translateKey.Invalidate();
            _chatgptKey.Invalidate();
            _geminiKey.Invalidate();
            _claudeKey.Invalidate();
            _enterKey.Invalidate();
            _cleanKey.Invalidate();
            _windowKey.Invalidate();
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
            _caption.Invalidate();
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
            _caption.Invalidate();
        } else if (_smoothed > 0.01f) {
            _smoothed *= 0.86f;
            Array.Clear(_bars);
            _statusCard.Invalidate();
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

    private void OnCaptionPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        e.Graphics.Clear(HudTheme.Window);
        int pad = HudTheme.Dip(this, 22);
        int cy = _caption.Height / 2;
        Color accent = HudTheme.PhaseAccent(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        float pulse = _host.Phase == AppPhase.Listening
            ? 0.55f + 0.45f * MathF.Sin(_time * 5.5f)
            : 1f;
        float glow = HudTheme.DipF(this, 7)
            + HudTheme.DipF(this, 3) * (pulse - 0.55f)
            + HudTheme.DipF(this, 4) * _smoothed * pulse;
        int w = _caption.Width;
        int cx = pad + HudTheme.Dip(this, 6);
        if (Loc.Rtl) {
            cx = w - cx;
        }
        using (var glowBrush = new SolidBrush(Color.FromArgb((int)(36 + 40 * _smoothed), accent))) {
            e.Graphics.FillEllipse(glowBrush, cx - glow, cy - glow - 6, glow * 2, glow * 2);
        }
        using (var dot = new SolidBrush(accent)) {
            float d = HudTheme.DipF(this, 4.5f);
            e.Graphics.FillEllipse(dot, cx - d, cy - d - 6, d * 2, d * 2);
        }

        // Caption buttons sit on the left for Arabic (PlaceCaptionButtons), so measure
        // the free space from whichever side they are on.
        int textLeft = pad + HudTheme.Dip(this, 22);
        int buttonsEdge = Loc.Rtl ? w - _min.Right : _min.Left;
        int textRight = Math.Max(textLeft + 40, buttonsEdge - HudTheme.Dip(this, 8));
        TextRenderer.DrawText(e.Graphics, "EchoType", HudTheme.Caption,
            Loc.Mirror(new Rectangle(textLeft, HudTheme.Dip(this, 10), textRight - textLeft, HudTheme.Dip(this, 24)), w),
            HudTheme.Body, Loc.Flags(TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));
        TextRenderer.DrawText(e.Graphics, _host.StatusText, HudTheme.Small,
            Loc.Mirror(new Rectangle(textLeft, HudTheme.Dip(this, 34), textRight - textLeft, HudTheme.Dip(this, 20)), w),
            HudTheme.Muted, Loc.Flags(TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));

        using var hair = new Pen(HudTheme.Border, 1f);
        e.Graphics.DrawLine(hair, 0, _caption.Height - 1, _caption.Width, _caption.Height - 1);
    }

    private void OnStatusCardPaint(object? sender, PaintEventArgs e) {
        HudTheme.Prepare(e.Graphics);
        int shadow = HudTheme.Dip(_statusCard, 7);
        var pill = Rectangle.Inflate(_statusCard.ClientRectangle, -shadow, -shadow);
        int left = pill.Left + HudTheme.Dip(this, 28);
        int top = pill.Top + HudTheme.Dip(this, 16);
        string phase = HudTheme.PhaseLabel(_host.Phase, _host.LoggedIn, _host.Online, _host.PageReady);
        string body = _host.ModelName + " · " + Loc.T(
            !_host.Online ? "No internet"
                : !_host.LoggedIn ? "Sign in to dictate"
                : _host.PageReady ? "Signed in"
                : "Waiting for page");
        int cw = _statusCard.Width;
        TextRenderer.DrawText(e.Graphics, phase, HudTheme.Display,
            Loc.Mirror(new Rectangle(left, top, pill.Width - HudTheme.Dip(this, 48), HudTheme.Dip(this, 32)), cw),
            HudTheme.Body, Loc.Flags(TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));
        TextRenderer.DrawText(e.Graphics, body, HudTheme.Small,
            Loc.Mirror(new Rectangle(left, top + HudTheme.Dip(this, 32), pill.Width - HudTheme.Dip(this, 48), HudTheme.Dip(this, 20)), cw),
            HudTheme.Muted, Loc.Flags(TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding));

        if (_host.Phase == AppPhase.Listening) {
            var wave = Loc.Mirror(new Rectangle(
                pill.Right - HudTheme.Dip(this, 168),
                pill.Top + HudTheme.Dip(this, 18),
                HudTheme.Dip(this, 132),
                HudTheme.Dip(this, 36)), cw);
            HudTheme.DrawWave(e.Graphics, wave, _bars, HudTheme.Teal, HudTheme.DipF(this, 18));
        }
    }

    private void OnCaptionMouseDown(object? sender, MouseEventArgs e) {
        if (e.Button != MouseButtons.Left || WindowState == FormWindowState.Maximized) {
            return;
        }
        _dragging = true;
        _dragX = e.X;
        _dragY = e.Y;
    }

    private void OnCaptionMouseMove(object? sender, MouseEventArgs e) {
        if (!_dragging) {
            return;
        }
        Location = new Point(Location.X + e.X - _dragX, Location.Y + e.Y - _dragY);
    }

    private void PlaceCaptionButtons() {
        int pad = HudTheme.Dip(this, 14);
        _close.Size = new Size(HudTheme.Dip(this, 36), HudTheme.Dip(this, 28));
        _min.Size = _close.Size;
        int top = (_caption.Height - _close.Height) / 2;
        if (Loc.Rtl) {
            _close.Location = new Point(pad, top);
            _min.Location = new Point(_close.Right + HudTheme.Dip(this, 6), top);
            return;
        }
        _close.Location = new Point(_caption.Width - _close.Width - pad, top);
        _min.Location = new Point(_close.Left - _min.Width - HudTheme.Dip(this, 6), top);
    }

    private void Relayout() {
        int width = Math.Max(200, _scrollHost.ClientSize.Width - 4);
        _flow.Width = width;
        foreach (Control child in _flow.Controls) {
            child.Width = Math.Max(120, width - _flow.Padding.Horizontal);
            if (child is HudCard card && card != _statusCard) {
                LayoutStacked(card);
            }
        }
        _statusCard.Height = HudTheme.Dip(this, 168);
        PlaceOnCard(_statusCard, _loginButton, 18);
        _flow.PerformLayout();
    }

    private HudCard MakeCard() {
        var card = new HudCard {
            Margin = new Padding(4, 4, 4, 8),
            Width = 480,
        };
        _flow.Controls.Add(card);
        return card;
    }

    private static void AddCaption(HudCard card, string text) {
        // Arabic: a full-width, right-aligned label (LayoutStacked sets the width).
        var label = new Label {
            Text = text,
            AutoSize = !Loc.Rtl,
            Font = HudTheme.Section,
            ForeColor = HudTheme.Title,
            BackColor = HudTheme.Card,
            Margin = new Padding(0, 0, 0, 10),
            UseMnemonic = false,
        };
        if (Loc.Rtl) {
            // RightToLeft mirrors TextAlign, so MiddleLeft lands on the right edge.
            label.RightToLeft = RightToLeft.Yes;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Height = TextRenderer.MeasureText(text, label.Font).Height + 4;
        }
        card.Controls.Add(label);
        LayoutStacked(card);
    }

    private static void AddControl(HudCard card, Control control, int height) {
        control.Margin = new Padding(0, 0, 0, 8);
        control.Height = height;
        control.BackColor = HudTheme.Card;
        card.Controls.Add(control);
        LayoutStacked(card);
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
        AddControl(card, row, 56);
        return row;
    }

    private HudToggleRow AddToggle(HudCard card, string text, Action<HudToggleRow> changed) {
        var row = new HudToggleRow { Text = text, Font = Font };
        row.CheckedChanged += (_, _) => {
            if (!_syncing) {
                changed(row);
            }
        };
        AddControl(card, row, 44);
        return row;
    }

    private static HudButton MakeButton(string text, HudButton.Kind kind) {
        return new HudButton {
            Text = text,
            StyleKind = kind,
            Font = HudTheme.SmallBold,
            Height = 38,
        };
    }

    private static void FinishCard(HudCard card) => LayoutStacked(card);

    private static void LayoutStacked(HudCard card) {
        int x = card.Padding.Left;
        int y = card.Padding.Top;
        int inner = Math.Max(40, card.ClientSize.Width - card.Padding.Horizontal);
        foreach (Control child in card.Controls) {
            if (child == null) {
                continue;
            }
            child.Left = x;
            child.Top = y;
            child.Width = inner;
            y += child.Height + child.Margin.Bottom;
        }
        card.Height = y + card.Padding.Bottom;
    }

    private static void PlaceOnCard(HudCard card, Control control, int bottomPad) {
        int inner = Math.Max(40, card.ClientSize.Width - card.Padding.Horizontal);
        control.Width = inner;
        control.Left = card.Padding.Left;
        control.Top = card.Height - card.Padding.Bottom - control.Height + 6;
        if (!card.Controls.Contains(control)) {
            card.Controls.Add(control);
        }
        _ = bottomPad;
    }

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
        public override string ToString() => Loc.F("Translate to {0}", Name);
    }

    private static string HotkeyLabel(int vk) => vk <= 0 ? Loc.T("(none)") : HotkeyNames.For(vk);
}
