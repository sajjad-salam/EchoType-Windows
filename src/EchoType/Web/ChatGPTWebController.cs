using System.Drawing;
using System.Net.NetworkInformation;
using Microsoft.Web.WebView2.Core;

namespace EchoType.Web;

internal enum ReadyOutcome {
    Ready,
    LoggedOut,
    Offline,
    Timeout,
    NotReady,
    RuntimeMissing,
}

internal sealed class WebView2RuntimeMissingException : Exception {
    public WebView2RuntimeMissingException()
        : base("Microsoft Edge WebView2 runtime is not installed.") { }
}

/// <summary>
/// Owns the hidden WebView2 hosting chatgpt.com; the same window is shown for login
/// (ChatGPTWebController.swift analog).
///
/// Hidden mode parks the borderless form fully offscreen with WS_EX_NOACTIVATE —
/// Chromium keeps rendering because native window occlusion calculation is disabled
/// in the browser arguments (the macOS port needed a 2 pt on-screen sliver because
/// WebKit freezes its pipeline on any invisibility; Chromium's equivalent knob is
/// the occlusion tracker). The form is never Hidden()/Visible=false — that suspends
/// rendering, the WebView2 analog of WebKit's frozen rAF.
/// </summary>
internal sealed class ChatGPTWebController : IDisposable {

    public const string ChatUrl = "https://chatgpt.com/";
    private const int WebViewWidth = 1100;
    private const int WebViewHeight = 760;
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>Pins Page Visibility + focus to visible/focused; hooks getUserMedia + console
    /// early. Verbatim port of visibilitySpoofScript — the rAF shim is belt-and-braces
    /// now (occlusion calc is disabled), but costs nothing and guards against the
    /// Chromium flag regressing.</summary>
    private static string VisibilitySpoofScript { get; } = """
        (function () {
          try {
            Object.defineProperty(Document.prototype, 'visibilityState', { get: function () { return 'visible'; } });
            Object.defineProperty(Document.prototype, 'hidden', { get: function () { return false; } });
            Document.prototype.hasFocus = function () { return true; };
            document.addEventListener('visibilitychange', function (e) { e.stopImmediatePropagation(); }, true);
            window.addEventListener('pagehide', function (e) { e.stopImmediatePropagation(); }, true);
            window.addEventListener('blur', function (e) { e.stopImmediatePropagation(); }, true);
          } catch (e) {}
          try {
            // Headless windows can have their animation pipeline starved — chatgpt.com's
            // dictation flow awaits an animation frame and would silently stall. Race
            // every rAF against a 33 ms timer so callbacks always run; real frames win
            // when the window is visible.
            if (!window.__etRAF) {
              window.__etRAF = true;
              const nativeRAF = window.requestAnimationFrame.bind(window);
              const nativeCAF = window.cancelAnimationFrame.bind(window);
              let nextId = 1;
              const pending = new Map();
              window.requestAnimationFrame = function (cb) {
                const id = nextId++;
                const fire = function (ts) {
                  const p = pending.get(id);
                  if (!p) return;
                  pending.delete(id);
                  nativeCAF(p.raf);
                  clearTimeout(p.timer);
                  try { cb(ts); } catch (e) { setTimeout(function () { throw e; }, 0); }
                };
                const raf = nativeRAF(fire);
                const timer = setTimeout(function () { fire(performance.now()); }, 33);
                pending.set(id, { raf: raf, timer: timer });
                return id;
              };
              window.cancelAnimationFrame = function (id) {
                const p = pending.get(id);
                if (!p) return;
                pending.delete(id);
                nativeCAF(p.raf);
                clearTimeout(p.timer);
              };
            }
          } catch (e) {}
          try {
            if (navigator.mediaDevices && !navigator.mediaDevices.__etWrapped) {
              const orig = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
              navigator.mediaDevices.getUserMedia = function (c) {
                window.__etGUM = 'requested';
                return orig(c).then(function (s) { window.__etGUM = 'ok'; return s; })
                              .catch(function (e) { window.__etGUM = 'err:' + e.name + ':' + e.message; throw e; });
              };
              navigator.mediaDevices.__etWrapped = true;
            }
          } catch (e) {}
          try {
            if (!window.__etLogs) {
              window.__etLogs = [];
              const push = function (kind, args) {
                try {
                  const msg = Array.prototype.map.call(args, function (a) {
                    return (a && a.stack) ? a.stack.split('\n')[0] : String(a);
                  }).join(' ');
                  window.__etLogs.push(kind + ': ' + msg.slice(0, 200));
                  if (window.__etLogs.length > 20) window.__etLogs.shift();
                } catch (e) {}
              };
              const origError = console.error.bind(console);
              console.error = function () { push('error', arguments); origError.apply(null, arguments); };
              const origWarn = console.warn.bind(console);
              console.warn = function () { push('warn', arguments); origWarn.apply(null, arguments); };
              window.addEventListener('unhandledrejection', function (e) {
                push('rejection', [e.reason && (e.reason.message || e.reason)]);
              });
              window.addEventListener('error', function (e) { push('jserror', [e.message]); });
            }
          } catch (e) {}
        })();
        """;

    private readonly WebViewHostForm _form = new();
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webview;
    private DictationDriver? _driver;
    private volatile bool _loading;
    private bool _loginVisible;
    private bool _showingOfflinePage;
    private bool _isOnline;
    private bool _inited;
    private bool _disposed;
    private Task<ReadyOutcome>? _ensureTask;

    public ChatGPTWebController() {
        _isOnline = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public DictationDriver? Driver => _driver;

    /// <summary>True once a webview exists to start dictation on (decides Waking vs Engaging).</summary>
    public bool HasWebView => _webview != null;

    public bool IsOnline => _isOnline;

    public bool IsLoginWindowVisible => _loginVisible;

    public event Action<bool>? LoginStateChanged;
    public event Action<bool>? ReachabilityChanged;

    // ------------------------------------------------------------------
    // Readiness (mac ensureReady / waitUntilInteractive parity)
    // ------------------------------------------------------------------

    public async Task<ReadyOutcome> EnsureReadyAsync() {
        if (_disposed) {
            return ReadyOutcome.NotReady;
        }
        if (_ensureTask != null) {
            return await _ensureTask;
        }
        var task = EnsureReadyCoreAsync();
        _ensureTask = task;
        try {
            return await task;
        } finally {
            if (_ensureTask == task) {
                _ensureTask = null; // allow later calls to re-probe quickly
            }
        }
    }

    private async Task<ReadyOutcome> EnsureReadyCoreAsync() {
        // Offline: fail fast instead of loading a blank page for 25 s (mac parity).
        if (!_isOnline && _webview == null) {
            Log.Write("webview: ensureReady while offline — failing fast");
            return ReadyOutcome.Offline;
        }

        try {
            if (!_inited) {
                await InitAsync();
            }
            if (_webview == null || _driver == null) {
                return ReadyOutcome.NotReady;
            }
            if (_loading || _showingOfflinePage) {
                NavigateToChat();
                return await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
            }
        } catch (WebView2RuntimeMissingException) {
            return ReadyOutcome.RuntimeMissing;
        } catch (Exception ex) {
            Log.Write("webview: init failed: " + ex.Message);
            return ReadyOutcome.NotReady;
        }

        // Already loaded: single state probe. On probe failure, reload once (mac parity).
        try {
            var st = await _driver.StateAsync();
            LoginStateChanged?.Invoke(st.LoggedIn);
            return st.LoggedIn ? ReadyOutcome.Ready : ReadyOutcome.LoggedOut;
        } catch (DriverException ex) {
            Log.Write("webview: state probe failed (" + ex.Message + "), reloading");
            Unload();
            return await EnsureReadyCoreAsync();
        }
    }

    /// <summary>Self-heal path for when chatgpt.com's dictation state machine wedges.</summary>
    public async Task ReloadInBackgroundAsync() {
        Unload();
        try {
            var outcome = await EnsureReadyAsync();
            Log.Write("webview: self-heal reload -> " + outcome);
        } catch (Exception ex) {
            Log.Write("webview: self-heal reload failed: " + ex.Message);
        }
    }

    private async Task<ReadyOutcome> WaitUntilInteractiveAsync(TimeSpan timeout) {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true) {
            if (!_loading || _driver == null) {
                return ReadyOutcome.NotReady; // unloaded mid-load
            }
            if (!_isOnline) {
                Log.Write("webview: went offline during load");
                return ReadyOutcome.Offline;
            }
            try {
                var st = await _driver.StateAsync();
                if (st.LoggedIn) {
                    Log.Write("webview: ready, logged in");
                    _loading = false;
                    LoginStateChanged?.Invoke(true);
                    if (_loginVisible) {
                        HideLoginWindow(); // logged in while the login window was up
                    }
                    return ReadyOutcome.Ready;
                }
                if (DateTime.UtcNow > deadline) {
                    Log.Write("webview: ready but logged OUT");
                    _loading = false;
                    LoginStateChanged?.Invoke(false);
                    return ReadyOutcome.LoggedOut;
                }
            } catch (DriverException ex) {
                if (DateTime.UtcNow > deadline) {
                    Log.Write("webview: load timeout (" + ex.Message + ")");
                    _loading = false;
                    return ReadyOutcome.Timeout;
                }
            }
            await Task.Delay(500);
        }
    }

    /// <summary>Fire-and-forget variant used by the login window path.</summary>
    private async Task WaitAndLogInteractiveAsync() {
        var outcome = await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
        Log.Write("webview: login-window load -> " + outcome);
    }

    // ------------------------------------------------------------------
    // Init / teardown
    // ------------------------------------------------------------------

    private async Task InitAsync() {
        string version;
        try {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        } catch (Exception ex) {
            Log.Write("webview: WebView2 runtime not found: " + ex.Message);
            throw new WebView2RuntimeMissingException();
        }

        // Real Edge UA — the default WebView2 UA carries tokens that Google SSO and
        // other embedded-browser login blocks reject. Composed from the installed
        // runtime so it never drifts stale.
        string major = version.Split('.')[0];
        string ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + $"Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0";

        var options = new CoreWebView2EnvironmentOptions {
            // Chromium deprioritizes offscreen/occluded windows (frozen rAF + throttled
            // timers — the failure mode the macOS port fought with a 2 pt window sliver).
            AdditionalBrowserArguments =
                "--disable-features=CalculateNativeWinOcclusion "
                + "--disable-background-timer-throttling "
                + "--disable-renderer-backgrounding "
                + "--disable-ipc-flooding-protection",
            Language = "en-US",
        };
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoType", "WebView2");
        _env = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

        // One form, two modes: parked offscreen (hidden) or centered (login).
        // Never Hidden()/Visible=false — that suspends rendering.
        _form.Show();
        ApplyHiddenMode();

        _controller = await _env.CreateCoreWebView2ControllerAsync(_form.Handle);
        _controller.Bounds = new Rectangle(0, 0, WebViewWidth, WebViewHeight);
        _controller.IsVisible = true; // forever; IsVisible=false suspends the renderer
        _controller.DefaultBackgroundColor = Color.White;
        _webview = _controller.CoreWebView2;

        _webview.Settings.UserAgent = ua;
        _webview.Settings.IsStatusBarEnabled = false;
        _webview.Settings.IsZoomControlEnabled = false;

        _ = _webview.AddScriptToExecuteOnDocumentCreatedAsync(VisibilitySpoofScript);
        _ = _webview.AddScriptToExecuteOnDocumentCreatedAsync(DictationDriver.UserScriptSource);

        _webview.PermissionRequested += OnPermissionRequested;
        _webview.NavigationStarting += OnNavigationStarting;
        _webview.NavigationCompleted += OnNavigationCompleted;
        _webview.WebMessageReceived += OnWebMessageReceived;
        _webview.ProcessFailed += OnProcessFailed;
        _webview.NewWindowRequested += OnNewWindowRequested;
        _form.Activated += OnFormActivated;

        _driver = new DictationDriver(_webview);
        _inited = true;
        Log.Write("webview: initialized (runtime " + version + ")");
    }

    /// <summary>Frees the browser process; cookies stay on disk (mac unload parity).</summary>
    public void Unload() {
        Log.Write("webview: unloading");
        _loading = false;
        _inited = false;
        _showingOfflinePage = false;
        _driver = null;
        if (_webview != null) {
            _webview.PermissionRequested -= OnPermissionRequested;
            _webview.NavigationStarting -= OnNavigationStarting;
            _webview.NavigationCompleted -= OnNavigationCompleted;
            _webview.WebMessageReceived -= OnWebMessageReceived;
            _webview.ProcessFailed -= OnProcessFailed;
            _webview.NewWindowRequested -= OnNewWindowRequested;
            _webview = null;
        }
        _form.Activated -= OnFormActivated;
        _controller?.Close();
        _controller = null;
        _env = null;
    }

    // ------------------------------------------------------------------
    // Login window
    // ------------------------------------------------------------------

    /// <summary>Menu-driven only; never auto-called, so logged-out/offline can't loop it open.</summary>
    public void ShowLoginWindow() {
        _loginVisible = true;
        if (_inited && _webview != null) {
            if (!_isOnline) {
                PresentOfflinePage();
            } else {
                if (_showingOfflinePage) {
                    RetryLoad();
                } else {
                    _ = EnsureReadyAsync();
                }
            }
            ApplyLoginMode();
            return;
        }
        _ = ShowLoginWindowCoreAsync();
    }

    private async Task ShowLoginWindowCoreAsync() {
        try {
            if (!_inited) {
                await InitAsync();
            }
            if (!_isOnline) {
                PresentOfflinePage();
            } else {
                NavigateToChat();
                _ = WaitAndLogInteractiveAsync();
            }
        } catch (WebView2RuntimeMissingException) {
            ShowRuntimeMissingDialog();
        } catch (Exception ex) {
            Log.Write("webview: login init failed: " + ex.Message);
        }
        ApplyLoginMode();
    }

    public void HideLoginWindow() {
        if (!_loginVisible) {
            return;
        }
        _loginVisible = false;
        ApplyHiddenMode();
        Log.Write("webview: login window hidden");
    }

    private void ApplyHiddenMode() {
        // Park fully offscreen; occlusion calculation is disabled so Chromium keeps
        // rendering. WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW keep it out of Alt+Tab and
        // unable to steal focus.
        _form.Location = new Point(-32000, -32000);
        long ex = Native.NativeMethods.GetWindowLongPtrW(_form.Handle, Native.NativeMethods.GWL_EXSTYLE);
        Native.NativeMethods.SetWindowLongPtrW(_form.Handle, Native.NativeMethods.GWL_EXSTYLE,
            ex | Native.NativeMethods.WS_EX_NOACTIVATE | Native.NativeMethods.WS_EX_TOOLWINDOW);
        if (_controller != null) {
            _controller.IsVisible = true;
        }
    }

    private void ApplyLoginMode() {
        long ex = Native.NativeMethods.GetWindowLongPtrW(_form.Handle, Native.NativeMethods.GWL_EXSTYLE);
        Native.NativeMethods.SetWindowLongPtrW(_form.Handle, Native.NativeMethods.GWL_EXSTYLE,
            ex & ~Native.NativeMethods.WS_EX_NOACTIVATE & ~Native.NativeMethods.WS_EX_TOOLWINDOW);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
        _form.Location = new Point(
            area.Left + Math.Max(0, (area.Width - _form.Width) / 2),
            area.Top + Math.Max(0, (area.Height - _form.Height) / 2));
        _form.Activate();
        _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    private void OnFormActivated(object? sender, EventArgs e) {
        if (_loginVisible) {
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
    }

    // ------------------------------------------------------------------
    // Reachability
    // ------------------------------------------------------------------

    private void OnNetworkAddressChanged(object? sender, EventArgs e) {
        bool online = NetworkInterface.GetIsNetworkAvailable();
        if (online == _isOnline) {
            return;
        }
        _isOnline = online;
        Log.Write("reachability: " + (online ? "online" : "offline"));
        ReachabilityChanged?.Invoke(online);
        if (online) {
            RecoverFromOffline();
        }
    }

    private void RecoverFromOffline() {
        if (_showingOfflinePage) {
            RetryLoad();
        } else if (!_inited) {
            _ = EnsureReadyAsync();
        }
    }

    private void RetryLoad() {
        if (_webview == null) {
            ShowLoginWindow();
            return;
        }
        _showingOfflinePage = false;
        NavigateToChat();
        _ = WaitAndLogInteractiveAsync();
    }

    // ------------------------------------------------------------------
    // WebView2 events
    // ------------------------------------------------------------------

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e) {
        try {
            string host = new Uri(e.Uri).Host;
            bool isChatGPT = host.EndsWith("chatgpt.com", StringComparison.OrdinalIgnoreCase)
                          || host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase);
            if (isChatGPT && e.PermissionKind == CoreWebView2PermissionKind.Microphone) {
                e.State = CoreWebView2PermissionState.Allow;
                e.Handled = true;
                Log.Write("webview: mic permission granted for " + host);
            }
        } catch (Exception ex) {
            Log.Write("webview: permission handler error: " + ex.Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
        Log.Write("webview: navigate " + e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
        Log.Write($"webview: navigation completed ok={e.IsSuccess}");
        if (e.IsSuccess) {
            _showingOfflinePage = false;
            _ = ProbeAfterNavigationAsync();
        } else if (!_isOnline) {
            // mac handleNavigationFailure parity: show the offline page in the login
            // window; readiness waiters fail through the offline check in their loop.
            if (_loginVisible) {
                PresentOfflinePage();
            }
        }
    }

    /// <summary>mac didFinish parity: 1.5 s after a navigation, probe login state; a
    /// logged-in session auto-hides a visible login window.</summary>
    private async Task ProbeAfterNavigationAsync() {
        await Task.Delay(1500);
        if (_driver == null || _loading) {
            return;
        }
        try {
            var st = await _driver.StateAsync();
            Log.Write("webview: post-navigation probe, loggedIn=" + st.LoggedIn);
            LoginStateChanged?.Invoke(st.LoggedIn);
            if (st.LoggedIn && _loginVisible) {
                HideLoginWindow();
            }
        } catch {
            // page mid-reload etc. — the readiness probes cover the flow
        }
    }

    private void PresentOfflinePage() {
        if (_webview == null) {
            return;
        }
        _showingOfflinePage = true;
        _loading = false;
        Log.Write("webview: presenting offline page");
        _webview.NavigateToString(OfflinePage.Html);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        if (e.TryGetWebMessageAsString() == "retry") {
            Log.Write("webview: retry from offline page");
            RetryLoad();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) {
        Log.Write($"webview: process failed ({e.ProcessFailedKind}) — unloading");
        Unload(); // next hotkey press does a fresh EnsureReadyAsync
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) {
        // Default popup shares this profile's cookies — required for Google SSO.
        Log.Write("webview: new window " + e.Uri);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) {
        // Screen changes can leave the parked window on a removed monitor — re-park.
        if (_inited && !_loginVisible) {
            ApplyHiddenMode();
        }
    }

    private void NavigateToChat() {
        _loading = true;
        _showingOfflinePage = false;
        Log.Write("webview: loading " + ChatUrl);
        _webview?.Navigate(ChatUrl);
    }

    public static void ShowRuntimeMissingDialog() {
        var result = MessageBox.Show(
            "EchoType needs the Microsoft Edge WebView2 runtime, which is not installed.\n\n"
            + "Install it from:\n" + RuntimeDownloadUrl + "\n\nOpen the download page now?",
            "EchoType — WebView2 runtime required",
            MessageBoxButtons.YesNo, MessageBoxIcon.Error);
        if (result == DialogResult.Yes) {
            try {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo {
                    FileName = RuntimeDownloadUrl,
                    UseShellExecute = true,
                });
            } catch { /* nothing sensible to do */ }
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Unload();
        _form.Dispose();
    }

    /// <summary>Borderless host: never activates when parked offscreen; no chrome, no taskbar.
    /// FormBorderStyle is fixed at None — changing it would recreate the HWND and
    /// detach the WebView2 controller.</summary>
    private sealed class WebViewHostForm : Form {
        public WebViewHostForm() {
            Text = "EchoType — ChatGPT Login";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ControlBox = false;
            Size = new Size(WebViewWidth, WebViewHeight);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= unchecked((int)Native.NativeMethods.WS_EX_TOOLWINDOW); // hidden-mode default; login clears it
                return cp;
            }
        }
    }
}
