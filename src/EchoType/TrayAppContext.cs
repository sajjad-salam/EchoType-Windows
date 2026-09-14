using System.Diagnostics;
using EchoType.Hotkey;
using EchoType.Input;
using EchoType.Native;
using EchoType.Web;

namespace EchoType;

/// <summary>
/// Owns the tray icon, the global hotkey, and the dictation state machine
/// (AppDelegate.swift analog, minus hands-free/HUD/history/settings which are
/// deferred out of the MVP).
/// </summary>
internal sealed class TrayAppContext : ApplicationContext {

    /// <summary>Releases shorter than this are a tap → cancel (mac tapThreshold).
    /// The mac double-tap → hands-free gesture is intentionally not ported.</summary>
    private const double TapThresholdSeconds = 0.35;

    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _loginItem;
    private readonly Control _marshal = new(); // UI-thread marshal target for the hook
    private readonly Settings _settings;
    private readonly ChatGPTWebController _web = new();
    private readonly HotkeyMonitor _hotkey = new();

    private AppPhase _phase = AppPhase.Idle;
    private bool _hotkeyHeld;
    private DateTime _holdStartedAt;
    private DateTime _releasedAt;
    private bool _loggedIn = true;
    private bool _online = true;
    private int _engagementFailures; // consecutive; 2+ triggers a page reload

    public TrayAppContext() {
        _settings = Settings.Load();

        var statusItem = new ToolStripMenuItem("Hold Right Ctrl to dictate") { Enabled = false };
        _loginItem = new ToolStripMenuItem("Open ChatGPT Login…", null, (_, _) => _web.ShowLoginWindow());
        var closeLoginItem = new ToolStripMenuItem("Close login window", null, (_, _) => _web.HideLoginWindow());

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_loginItem);
        menu.Items.Add(closeLoginItem);
#if DEBUG
        menu.Items.Add(new ToolStripMenuItem("Start dictation (debug)", null, (_, _) => DebugStart()));
#endif
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log", null, (_, _) => OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit EchoType", null, (_, _) => ExitThread());

        _tray = new NotifyIcon {
            Icon = TrayIcons.For(_phase, _loggedIn, _online),
            Text = "EchoType — hold Right Ctrl to dictate",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _web.ShowLoginWindow();

        _web.LoginStateChanged += OnLoginStateChanged;
        _web.ReachabilityChanged += OnReachabilityChanged;

        if (!_hotkey.Start((uint)_settings.HotkeyVk, _marshal)) {
            MessageBox.Show("EchoType could not install its global keyboard hook.", "EchoType",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _hotkey.HoldStart += OnHoldStart;
        _hotkey.HoldEnd += OnHoldEnd;
        _hotkey.CancelRequested += OnCancelRequested;

        Log.Write($"launch: EchoType for Windows started (hotkey VK=0x{_settings.HotkeyVk:X2})");
        _ = WarmupAsync(); // alwaysReady: load ChatGPT at launch (mac applyPolicyAtLaunch)
    }

    // ------------------------------------------------------------------
    // Hotkey → state machine
    // ------------------------------------------------------------------

    private void OnHoldStart() {
        switch (_phase) {
            case AppPhase.Idle:
                _hotkeyHeld = true;
                _holdStartedAt = DateTime.UtcNow;
                _releasedAt = default;
                Log.Write("dictation: key down");
                SetPhase(_web.HasWebView ? AppPhase.Engaging : AppPhase.Waking);
                _ = StartDictationSessionAsync();
                break;
            case AppPhase.Waking:
            case AppPhase.Engaging:
                _hotkeyHeld = true; // re-press before ready — keep waiting
                Log.Write("dictation: second press while opening");
                break;
            default:
                Log.Write($"dictation: key down ignored, phase {_phase} busy");
                break;
        }
    }

    private void OnHoldEnd() {
        _hotkeyHeld = false;
        _releasedAt = DateTime.UtcNow;
        double held = HeldSeconds();

        switch (_phase) {
            case AppPhase.Listening:
                if (held >= TapThresholdSeconds) {
                    _ = FinishListeningAsync();
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

    private void OnCancelRequested() {
        // Physical Esc swallowed while Listening — the no-HUD cancel path.
        if (_phase == AppPhase.Listening) {
            _ = CancelAndResetAsync("cancelled with Esc");
        }
    }

    private double HeldSeconds() {
        if (_holdStartedAt == default) {
            return 0;
        }
        DateTime end = _releasedAt == default ? DateTime.UtcNow : _releasedAt;
        return end.Subtract(_holdStartedAt).TotalSeconds;
    }

    private async Task StartDictationSessionAsync() {
        var outcome = await _web.EnsureReadyAsync();

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
            SetPhase(AppPhase.Idle);
            return;
        }

        SetPhase(AppPhase.Engaging);
        await OpenMicrophoneAsync();
    }

    private async Task OpenMicrophoneAsync() {
        var driver = _web.Driver;
        if (driver == null) {
            HandleFailure(new DriverException(DriverFailure.NotReady, "no driver"));
            return;
        }
        try {
            await driver.StartDictationAsync();
        } catch (DriverException ex) {
            HandleFailure(ex);
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
            await FinishListeningAsync();
        }
    }

    private async Task FinishListeningAsync() {
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
            transcript = await driver.AwaitTranscriptAsync(TimeSpan.FromSeconds(HeldSeconds()));
            await driver.ClearComposerAsync();
        } catch (DriverException ex) {
            // mac collectTranscript failure parity: cancel + clear, then report.
            try { await driver.CancelDictationAsync(); } catch { /* best effort */ }
            try { await driver.ClearComposerAsync(); } catch { /* best effort */ }
            HandleFailure(ex);
            return;
        }

        if (transcript.Length == 0) {
            Log.Write("dictation: empty transcript");
            Sounds.Error();
            ShowBalloon("Nothing transcribed — try holding the key longer.");
            ResetToIdle();
            return;
        }

        Log.Write($"dictation: delivering {transcript.Length} chars");
        switch (Paster.Deliver(transcript, _settings.KeepTranscriptOnClipboard)) {
            case Paster.Outcome.Pasted:
                Sounds.Pasted();
                ResetToIdle();
                break;
            case Paster.Outcome.CopiedToClipboard:
                Log.Write("dictation: no editable field focused, left on clipboard");
                Sounds.Pasted();
                ShowBalloon("Copied to clipboard — press Ctrl+V to paste.");
                ResetToIdle();
                break;
        }
    }

    private async Task CancelAndResetAsync(string reason) {
        Log.Write("dictation: " + reason);
        _hotkey.EscSwallowActive = false;
        try {
            if (_web.Driver != null) {
                await _web.Driver.CancelDictationAsync();
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
                break;
            case DriverFailure.Offline:
                message = "No internet connection — check your network";
                _online = false;
                UpdateStatusIcon();
                UpdateLoginMenuItem();
                break;
            case DriverFailure.Timeout:
                message = "ChatGPT didn't respond in time";
                break;
            case DriverFailure.ButtonNotFound:
                // The page wedges occasionally; only reload after two failures in a row.
                _engagementFailures++;
                if (_engagementFailures >= 2) {
                    message = $"Dictation glitched ({ex.Message}) — reloading ChatGPT, try again";
                    Log.Write($"dictation: {_engagementFailures} engagement failures, reloading webview to self-heal");
                    _engagementFailures = 0;
                    _ = _web.ReloadInBackgroundAsync();
                } else {
                    message = "Dictation didn't start — try again";
                }
                break;
            case DriverFailure.NotReady:
                message = "ChatGPT page isn't ready yet";
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
        _hotkey.EscSwallowActive = false;
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        SetPhase(AppPhase.Idle);
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
        _loginItem.Text = !_online ? "No internet connection — retry"
            : _loggedIn ? "ChatGPT: Logged In ✓ (open window)"
            : "Log in to ChatGPT…";
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
        OnHoldStart();
        await Task.Delay(5000);
        if (_phase == AppPhase.Listening) {
            _hotkeyHeld = false;
            _releasedAt = DateTime.UtcNow;
            await FinishListeningAsync();
        }
    }
#endif

    private static void OpenLog() {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = Log.FilePath,
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
