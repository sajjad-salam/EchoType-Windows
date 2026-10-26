using System.ComponentModel;
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
    /// <summary>Page loaded, but the selectors find no composer and no sign-in button.</summary>
    UiChanged,
}

internal sealed class WebView2RuntimeMissingException : Exception {
    public WebView2RuntimeMissingException()
        : base("Microsoft Edge WebView2 runtime is not installed.") { }
}

/// <summary>
/// Owns the hidden WebView2 hosting chatgpt.com or gemini.google.com; the same window
/// is shown for login (ChatGPTWebController.swift analog).
///
/// Hidden mode parks the form offscreen with WS_EX_NOACTIVATE after the compositor
/// has been created on a real monitor (creating it at -32000 yields a black surface).
/// Chromium keeps rendering because native window occlusion calculation is disabled
/// in the browser arguments. The form is never Hidden()/Visible=false — that suspends
/// rendering, the WebView2 analog of WebKit's frozen rAF.
///
/// The embedded engine is WebView2 (Chromium). We use a Chrome user-agent so ChatGPT,
/// Gemini, and Google SSO don't treat it as "Edg/" embedded-browser and blank the page.
/// </summary>
internal sealed class ChatGPTWebController : IDisposable {

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
        """ + "\n" + MicCapture.HookScript;

    private ChatSite _site;
    private WebViewHostForm _form = new();
    private CoreWebView2Environment? _env;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webview;
    private string? _browserExecutableFolder;
    private DictationDriver? _driver;
    private volatile bool _loading;
    private bool _loginVisible;
    private bool _showingOfflinePage;
    private bool _isOnline;
    private bool _inited;
    private bool _disposed;
    private bool _interactive;
    private int _navGeneration;
    private int _navCompletedGeneration;
    private Task<ReadyOutcome>? _ensureTask;
    private Task? _initTask;
    private readonly List<Form> _popups = [];

    // Standby page: a second, already-loaded copy of the chat site in the same profile.
    // Cancel swaps to it instantly and reloads the old page in the background, so the
    // next recording never waits on a reload.
    private readonly bool _keepStandby;
    private WebViewHostForm? _standbyForm;
    private CoreWebView2Controller? _standbyController;
    private CoreWebView2? _standbyWebview;
    private DictationDriver? _standbyDriver;
    private bool _standbyReady;
    private bool _standbyCreating;
    private int _standbyGeneration;

    public ChatGPTWebController(ChatSite? site = null, bool keepStandby = false) {
        _site = site ?? ChatSite.ChatGpt;
        _keepStandby = keepStandby;
        _isOnline = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        HookHostForm(_form);
        if (_keepStandby) {
            _standbyForm = new WebViewHostForm();
            HookHostForm(_standbyForm);
        }
    }

    private void HookHostForm(WebViewHostForm form) {
        form.Text = _site.LoginTitle;
        form.FormClosing += OnHostFormClosing;
        form.Resize += OnHostFormResize;
        form.LocationChanged += OnHostFormLocationChanged;
        form.Activated += OnFormActivated;
    }

    public DictationDriver? Driver => _driver;

    public ChatSite Site => _site;

    /// <summary>Successful prompt sends in this window's current ChatGPT thread.</summary>
    public int ChatThreadSends { get; set; }

    /// <summary>True after a failed send so the next prompt opens a new ChatGPT chat.</summary>
    public bool ForceNewChatThread { get; set; }

    /// <summary>Swap chatgpt.com / gemini.google.com. Cookies stay in each profile folder.</summary>
    public void SwitchSite(ChatSite site) {
        if (site.Id == _site.Id) {
            return;
        }
        Log.Write("webview: switching site " + _site.Id + " -> " + site.Id);
        HideLoginWindow();
        Unload();
        _ensureTask = null; // don't reuse the previous model's in-flight load
        _site = site;
        _form.Text = _site.LoginTitle;
        if (_standbyForm != null) {
            _standbyForm.Text = _site.LoginTitle;
        }
        ChatThreadSends = 0;
        ForceNewChatThread = false;
        SetMissingUiPart(null);
    }

    /// <summary>True once a webview exists to start dictation on (decides Waking vs Engaging).</summary>
    public bool HasWebView => _webview != null;

    /// <summary>
    /// True when the selected model's chat UI is fully up (signed in, composer,
    /// dictation mic). False from launch / a model switch until that finishes.
    /// </summary>
    public bool IsInteractive => _interactive;

    public bool IsOnline => _isOnline;

    public bool IsLoginWindowVisible => _loginVisible;

    /// <summary>
    /// What the selectors couldn't find after the last page load
    /// (<see cref="SelectorHealth.ComposerPart"/> / <see cref="SelectorHealth.MicPart"/>), or null.
    /// </summary>
    public string? MissingUiPart { get; private set; }

    public event Action<bool>? LoginStateChanged;
    public event Action<bool>? ReachabilityChanged;
    public event Action<bool>? InteractiveChanged;
    public event Action<string?>? MissingUiPartChanged;

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
            await EnsureInitAsync();
            if (_webview == null || _driver == null) {
                return ReadyOutcome.NotReady;
            }
            if (_showingOfflinePage || NeedsChatNavigation()) {
                NavigateToChat();
                return await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
            }
            if (_loading) {
                return await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
            }
        } catch (WebView2RuntimeMissingException) {
            return ReadyOutcome.RuntimeMissing;
        } catch (Exception ex) {
            Log.Write("webview: init failed: " + ex.Message);
            return ReadyOutcome.NotReady;
        }

        // Already loaded: single state probe. On probe failure, reload once (mac parity).
        // A missing composer is still-hydrating, not logged-out — wait instead of
        // flipping the tray to "log in" and rejecting the next record press.
        try {
            var st = await _driver.StateAsync();
            if (_loading) {
                // Cancel (or another caller) started a reload while we were probing.
                return await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
            }
            if (st.IsChatReady) {
                SetInteractive(true);
                LoginStateChanged?.Invoke(true);
                EnsureStandbyLoaded();
                return ReadyOutcome.Ready;
            }
            if (st.LoginMarker && !st.ComposerPresent) {
                SetInteractive(false);
                LoginStateChanged?.Invoke(false);
                return ReadyOutcome.LoggedOut;
            }
            _loading = true;
            return await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
        } catch (DriverException ex) {
            Log.Write("webview: state probe failed (" + ex.Message + "), reloading");
            Unload();
            return await EnsureReadyCoreAsync();
        }
    }

    /// <summary>Self-heal path for when the chat site's dictation state machine wedges.</summary>
    public async Task ReloadInBackgroundAsync() {
        Unload();
        try {
            var outcome = await EnsureReadyAsync();
            Log.Write("webview: self-heal reload -> " + outcome);
        } catch (Exception ex) {
            Log.Write("webview: self-heal reload failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Reloads ChatGPT/Gemini after the user cancels. Cancel often leaves the site's
    /// mic UI wedged, so the next recording would otherwise fail until a later
    /// self-heal reload. With a ready standby page this swaps to it instantly and
    /// reloads the wedged page in the background; otherwise the next hotkey joins
    /// EnsureReadyAsync and waits for the reload.
    /// </summary>
    public void ReloadAfterCancel() {
        if (_disposed) {
            return;
        }
        if (TrySwapToStandby()) {
            return;
        }
        Log.Write("webview: reloading " + _site.DisplayName + " after cancel");
        if (_webview == null || _driver == null) {
            _ = ReloadInBackgroundAsync();
            return;
        }
        BeginPageLoad();
        try {
            _webview.Reload();
        } catch (Exception ex) {
            Log.Write("webview: Reload() failed (" + ex.Message + "), navigating to chat");
            _webview.Navigate(_site.ChatUrl);
        }
        _ = EnsureReadyAsync();
    }

    /// <summary>
    /// Reloads the chat site and waits until the composer is usable. Used when a
    /// custom command times out so the prompt can be resent instead of dropped.
    /// Gemini may restore the last thread after this reload; StartNewChatAsync
    /// then clicks New chat before the retry is sent.
    /// </summary>
    public async Task<ReadyOutcome> RefreshChatAsync() {
        if (_disposed) {
            return ReadyOutcome.NotReady;
        }
        try {
            if (_webview != null && _driver != null) {
                Log.Write("webview: refreshing " + _site.ChatUrl + " for command retry");
                NavigateToChat();
                var outcome = await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
                if (outcome == ReadyOutcome.Ready) {
                    Log.Write("webview: chat refresh -> Ready");
                    return outcome;
                }
                Log.Write("webview: in-place refresh -> " + outcome + ", unloading");
            }
            Unload();
            return await EnsureReadyAsync();
        } catch (Exception ex) {
            Log.Write("webview: chat refresh failed: " + ex.Message);
            return ReadyOutcome.NotReady;
        }
    }

    private async Task<ReadyOutcome> WaitUntilInteractiveAsync(TimeSpan timeout) {
        DateTime deadline = DateTime.UtcNow + timeout;
        DateTime? signedInAt = null;
        int loginMarkerHits = 0;
        while (true) {
            if (_driver == null) {
                return ReadyOutcome.NotReady; // unloaded mid-load
            }
            if (!_loading) {
                return _interactive ? ReadyOutcome.Ready : ReadyOutcome.NotReady;
            }
            if (!_isOnline) {
                Log.Write("webview: went offline during load");
                SetInteractive(false);
                return ReadyOutcome.Offline;
            }
            if (_navCompletedGeneration != _navGeneration) {
                if (DateTime.UtcNow > deadline) {
                    Log.Write("webview: load timeout (navigation never completed)");
                    _loading = false;
                    SetInteractive(false);
                    return ReadyOutcome.Timeout;
                }
                await Task.Delay(150);
                continue;
            }
            try {
                var st = await _driver.StateAsync();
                if (_navCompletedGeneration != _navGeneration || !_loading) {
                    continue;
                }
                bool micSettled = st.LoggedIn && signedInAt is { } since
                    && DateTime.UtcNow - since >= TimeSpan.FromSeconds(3);
                if (st.IsChatReady || micSettled) {
                    Log.Write(st.IsChatReady
                        ? "webview: ready, logged in, dictation mic present"
                        : "webview: ready, logged in (composer up)");
                    _loading = false;
                    SetInteractive(true);
                    LoginStateChanged?.Invoke(true);
                    if (_loginVisible) {
                        HideLoginWindow(); // logged in while the login window was up
                    }
                    _ = CheckUiHealthAsync();
                    EnsureStandbyLoaded();
                    return ReadyOutcome.Ready;
                }
                if (st.LoggedIn) {
                    signedInAt ??= DateTime.UtcNow;
                    loginMarkerHits = 0;
                } else if (st.LoginMarker && !st.ComposerPresent) {
                    loginMarkerHits++;
                    // SPA boot can flash a sign-in control; require a couple of
                    // stable probes before treating this as actually logged out.
                    if (loginMarkerHits >= 4) {
                        Log.Write("webview: ready but logged OUT");
                        _loading = false;
                        SetInteractive(false);
                        LoginStateChanged?.Invoke(false);
                        return ReadyOutcome.LoggedOut;
                    }
                } else {
                    loginMarkerHits = 0;
                }
                if (DateTime.UtcNow > deadline) {
                    if (st.LoggedIn) {
                        Log.Write("webview: load timeout (signed in, page still settling)");
                        _loading = false;
                        SetInteractive(true);
                        LoginStateChanged?.Invoke(true);
                        _ = CheckUiHealthAsync();
                        return ReadyOutcome.Ready;
                    }
                    // No composer and no sign-in button on a fully loaded page is a
                    // UI change, not a logout — don't send the user to a login window.
                    if (!st.LoginMarker && await CheckUiHealthAsync() == SelectorHealth.ComposerPart) {
                        Log.Write("webview: load timeout (no composer and no sign-in button — UI changed?)");
                        _loading = false;
                        SetInteractive(false);
                        return ReadyOutcome.UiChanged;
                    }
                    Log.Write("webview: ready but logged OUT");
                    _loading = false;
                    SetInteractive(false);
                    LoginStateChanged?.Invoke(false);
                    return ReadyOutcome.LoggedOut;
                }
            } catch (DriverException ex) {
                if (DateTime.UtcNow > deadline) {
                    Log.Write("webview: load timeout (" + ex.Message + ")");
                    _loading = false;
                    SetInteractive(false);
                    return ReadyOutcome.Timeout;
                }
            }
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// Health check after a page load settles: can the selectors still find the
    /// composer and the mic button? A miss logs the page's buttons (so selectors.json
    /// can be patched from the log) and raises <see cref="MissingUiPartChanged"/>.
    /// </summary>
    private async Task<string?> CheckUiHealthAsync() {
        var driver = _driver;
        if (driver == null) {
            return MissingUiPart;
        }
        SelectorHealth health;
        try {
            health = await driver.HealthAsync();
        } catch (DriverException ex) {
            Log.Write("selectors: health check failed: " + ex.Message);
            return MissingUiPart;
        }
        if (!ReferenceEquals(driver, _driver)) {
            return MissingUiPart; // unloaded or switched site meanwhile
        }
        string? missing = health.MissingPart;
        if (missing != null) {
            Log.Write($"selectors: {_site.DisplayName} UI changed? can't find the {missing} ({health}). "
                + "Patch it in " + SelectorOverrides.FilePath + ". Buttons on page: "
                + Truncate(await driver.DumpButtonsAsync(), 2000));
        } else if (health.Composer && !health.ComposerExact && !health.Dictating) {
            Log.Write($"selectors: {_site.DisplayName} composer found only by the generic fallback — its composer selectors may be stale");
        } else if (MissingUiPart != null) {
            Log.Write($"selectors: {_site.DisplayName} health ok again");
        }
        if (!health.LoginMarker && health.DocReady) {
            SetMissingUiPart(missing);
        }
        return missing;
    }

    private void SetMissingUiPart(string? part) {
        if (part == MissingUiPart) {
            return;
        }
        MissingUiPart = part;
        MissingUiPartChanged?.Invoke(part);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Fire-and-forget variant used by the login window path.</summary>
    private async Task WaitAndLogInteractiveAsync() {
        var outcome = await WaitUntilInteractiveAsync(TimeSpan.FromSeconds(25));
        Log.Write("webview: login-window load -> " + outcome);
    }

    // ------------------------------------------------------------------
    // Standby page
    // ------------------------------------------------------------------

    /// <summary>
    /// Makes the ready standby page the active one and sends the old (possibly wedged)
    /// page to the background to reload as the next standby. False when no standby is
    /// ready — the caller falls back to an in-place reload.
    /// </summary>
    private bool TrySwapToStandby() {
        if (!_standbyReady || _loginVisible || !_isOnline
            || _standbyForm == null || _standbyController == null || _standbyWebview == null || _standbyDriver == null
            || _controller == null || _webview == null || _driver == null) {
            return false;
        }
        (_form, _standbyForm) = (_standbyForm, _form);
        (_controller, _standbyController) = (_standbyController, _controller);
        (_webview, _standbyWebview) = (_standbyWebview, _webview);
        (_driver, _standbyDriver) = (_standbyDriver, _driver);
        _navGeneration++;
        _navCompletedGeneration = _navGeneration; // the new active page is already loaded
        _loading = false;
        _showingOfflinePage = false;
        // The standby page sits on a fresh chat, not the old page's thread.
        ChatThreadSends = 0;
        ForceNewChatThread = false;
        SetInteractive(true);
        Log.Write("webview: cancel -> switched to the standby " + _site.DisplayName
            + " page, reloading the old one in the background");
        LoadStandby();
        _ = EnsureReadyAsync(); // quick probe: still signed in, mic still there
        return true;
    }

    /// <summary>Creates or loads the standby page once the active page is ready.</summary>
    private void EnsureStandbyLoaded() {
        if (!_keepStandby || _disposed || _standbyCreating) {
            return;
        }
        if (_standbyWebview == null) {
            _ = CreateStandbyAsync();
        } else if (!_standbyReady && _standbyGeneration == 0) {
            LoadStandby(); // created but never loaded, or dropped after a timeout
        }
    }

    private async Task CreateStandbyAsync() {
        var env = _env;
        var form = _standbyForm;
        if (env == null || form == null) {
            return;
        }
        _standbyCreating = true;
        CoreWebView2Controller? controller = null;
        try {
            if (!form.Visible) {
                // Like the main window, the compositor must start on a real monitor.
                // ShowWithoutActivation keeps focus where the user is typing.
                PlaceOnPrimaryMonitor(form);
                form.Show();
            }
            controller = await env.CreateCoreWebView2ControllerAsync(form.Handle);
            ApplyHiddenMode(form, controller);
            controller.DefaultBackgroundColor = Color.White;
            controller.IsVisible = true;
            var webview = controller.CoreWebView2;
            ApplyChromeIdentity(webview);
            var driver = new DictationDriver(webview, SelectorOverrides.Apply(_site.Id, _site.SelectorSet), _site.ChatUrl);
            await webview.AddScriptToExecuteOnDocumentCreatedAsync(VisibilitySpoofScript);
            await webview.AddScriptToExecuteOnDocumentCreatedAsync(driver.UserScript);
            if (_disposed || !ReferenceEquals(env, _env) || !ReferenceEquals(form, _standbyForm)) {
                controller.Close(); // unloaded or switched site meanwhile
                return;
            }
            HookWebView(webview);
            _standbyController = controller;
            _standbyWebview = webview;
            _standbyDriver = driver;
            Log.Write("webview: standby page created");
            LoadStandby();
        } catch (Exception ex) {
            Log.Write("webview: standby page failed: " + ex.Message);
            if (controller != null && !ReferenceEquals(controller, _standbyController)) {
                try { controller.Close(); } catch { /* best effort */ }
            }
        } finally {
            _standbyCreating = false;
        }
    }

    private void LoadStandby() {
        var webview = _standbyWebview;
        if (webview == null || _standbyDriver == null) {
            return;
        }
        int gen = ++_standbyGeneration;
        _standbyReady = false;
        Log.Write("webview: loading standby " + _site.ChatUrl);
        try {
            webview.Navigate(_site.ChatUrl);
        } catch (Exception ex) {
            Log.Write("webview: standby navigate failed: " + ex.Message);
            _standbyGeneration = 0;
            return;
        }
        _ = WatchStandbyAsync(gen);
    }

    /// <summary>Polls the standby page until its composer and mic are up.</summary>
    private async Task WatchStandbyAsync(int gen) {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        await Task.Delay(1500);
        while (gen == _standbyGeneration && _standbyDriver is { } driver) {
            try {
                var st = await driver.StateAsync();
                if (gen != _standbyGeneration) {
                    return;
                }
                if (st.IsChatReady) {
                    _standbyReady = true;
                    Log.Write("webview: standby page ready");
                    return;
                }
            } catch (DriverException) {
                // mid-navigation — keep polling
            }
            if (DateTime.UtcNow > deadline) {
                Log.Write("webview: standby page not ready in time — cancel will reload in place");
                _standbyGeneration = 0; // the next ready load retries it
                return;
            }
            await Task.Delay(1000);
        }
    }

    private void CloseStandby() {
        _standbyGeneration = 0;
        _standbyReady = false;
        _standbyDriver = null;
        if (_standbyWebview != null) {
            UnhookWebView(_standbyWebview);
            _standbyWebview = null;
        }
        try { _standbyController?.Close(); } catch { /* best effort */ }
        _standbyController = null;
    }

    private bool IsStandby(object? sender) =>
        sender != null && ReferenceEquals(sender, _standbyWebview);

    // ------------------------------------------------------------------
    // Init / teardown
    // ------------------------------------------------------------------

    private async Task EnsureInitAsync() {
        if (_inited) {
            return;
        }
        _initTask ??= InitAsync();
        try {
            await _initTask;
        } catch {
            _initTask = null;
            throw;
        }
    }

    private async Task InitAsync() {
        WebView2RuntimeLocator.EnsureNativeLoader();
        string version;
        try {
            version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        } catch (Exception evergreenEx) {
            _browserExecutableFolder = WebView2RuntimeLocator.FindBrowserExecutableFolder();
            if (_browserExecutableFolder == null) {
                Log.Write("webview: WebView2 runtime not found: " + evergreenEx.Message);
                throw new WebView2RuntimeMissingException();
            }
            try {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString(_browserExecutableFolder);
            } catch (Exception ex) {
                Log.Write("webview: WebView2 runtime not found: " + ex.Message);
                throw new WebView2RuntimeMissingException();
            }
            Log.Write("webview: evergreen missing, using fixed runtime");
        }

        var options = new CoreWebView2EnvironmentOptions {
            // Chromium deprioritizes offscreen/occluded windows (frozen rAF + throttled
            // timers — the failure mode the macOS port fought with a 2 pt window sliver).
            AdditionalBrowserArguments =
                "--disable-features=CalculateNativeWinOcclusion,msSmartScreenProtection,UserAgentClientHint "
                + "--disable-background-timer-throttling "
                + "--disable-renderer-backgrounding "
                + "--disable-ipc-flooding-protection",
            Language = "en-US",
        };
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EchoType", _site.ProfileFolder);
        _env = await CoreWebView2Environment.CreateAsync(_browserExecutableFolder, userDataFolder, options);

        // Create the compositor on a real monitor first. Parking at -32000 before
        // CreateCoreWebView2ControllerAsync leaves a black GPU surface forever.
        PlaceOnPrimaryMonitor();
        _form.Show();

        _controller = await _env.CreateCoreWebView2ControllerAsync(_form.Handle);
        _controller.DefaultBackgroundColor = Color.White;
        _controller.IsVisible = true;
        SyncWebViewBounds();
        _webview = _controller.CoreWebView2;

        ApplyChromeIdentity(_webview);
        _driver = new DictationDriver(_webview, SelectorOverrides.Apply(_site.Id, _site.SelectorSet), _site.ChatUrl);
        _ = _webview.AddScriptToExecuteOnDocumentCreatedAsync(VisibilitySpoofScript);
        _ = _webview.AddScriptToExecuteOnDocumentCreatedAsync(_driver.UserScript);

        HookWebView(_webview);

        _inited = true;
        Log.Write("webview: initialized " + _site.Id + " (runtime " + version
            + (_browserExecutableFolder != null ? ", fixed" : ", evergreen") + ")");

        NavigateToChat();
        if (!_loginVisible) {
            ApplyHiddenMode();
        } else {
            ApplyLoginMode();
        }
    }

    private void HookWebView(CoreWebView2 webview) {
        webview.PermissionRequested += OnPermissionRequested;
        webview.NavigationStarting += OnNavigationStarting;
        webview.NavigationCompleted += OnNavigationCompleted;
        webview.WebMessageReceived += OnWebMessageReceived;
        webview.ProcessFailed += OnProcessFailed;
        webview.NewWindowRequested += OnNewWindowRequested;
    }

    private void UnhookWebView(CoreWebView2 webview) {
        webview.PermissionRequested -= OnPermissionRequested;
        webview.NavigationStarting -= OnNavigationStarting;
        webview.NavigationCompleted -= OnNavigationCompleted;
        webview.WebMessageReceived -= OnWebMessageReceived;
        webview.ProcessFailed -= OnProcessFailed;
        webview.NewWindowRequested -= OnNewWindowRequested;
    }

    /// <summary>Chrome UA without the Edg/ token that makes Google/ChatGPT refuse embedded browsers.</summary>
    private void ApplyChromeIdentity(CoreWebView2 webview) {
        string major = "131";
        try {
            string version = _browserExecutableFolder == null
                ? CoreWebView2Environment.GetAvailableBrowserVersionString()
                : CoreWebView2Environment.GetAvailableBrowserVersionString(_browserExecutableFolder);
            major = version.Split('.')[0];
        } catch {
            // keep fallback
        }
        webview.Settings.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
            + $"Chrome/{major}.0.0.0 Safari/537.36";
        webview.Settings.IsStatusBarEnabled = true;
        webview.Settings.IsZoomControlEnabled = true;
        webview.Settings.AreDefaultContextMenusEnabled = true;
        webview.Settings.AreBrowserAcceleratorKeysEnabled = true;
    }

    /// <summary>Frees the browser process; cookies stay on disk (mac unload parity).</summary>
    public void Unload() {
        Log.Write("webview: unloading");
        _loading = false;
        _inited = false;
        _showingOfflinePage = false;
        _navGeneration = 0;
        _navCompletedGeneration = 0;
        SetInteractive(false);
        _driver = null;
        if (_webview != null) {
            UnhookWebView(_webview);
            _webview = null;
        }
        CloseStandby();
        foreach (var popup in _popups.ToArray()) {
            try { popup.Close(); } catch { /* best effort */ }
        }
        _popups.Clear();
        _controller?.Close();
        _controller = null;
        _env = null;
        _initTask = null;
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
            } else if (_showingOfflinePage || NeedsChatNavigation()) {
                NavigateToChat();
                _ = WaitAndLogInteractiveAsync();
            } else {
                _ = EnsureReadyAsync();
            }
            ApplyLoginMode();
            return;
        }
        _ = ShowLoginWindowCoreAsync();
    }

    private async Task ShowLoginWindowCoreAsync() {
        try {
            await EnsureInitAsync();
            if (!_isOnline) {
                PresentOfflinePage();
            } else if (NeedsChatNavigation()) {
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

    private void ApplyHiddenMode() => ApplyHiddenMode(_form, _controller);

    private static void ApplyHiddenMode(WebViewHostForm form, CoreWebView2Controller? controller) {
        // Park fully offscreen; occlusion calculation is disabled so Chromium keeps
        // rendering. WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW keep it out of Alt+Tab and
        // unable to steal focus.
        form.SuppressActivation = true;
        form.Location = new Point(-32000, -32000);
        long ex = Native.NativeMethods.GetWindowLongPtrW(form.Handle, Native.NativeMethods.GWL_EXSTYLE);
        Native.NativeMethods.SetWindowLongPtrW(form.Handle, Native.NativeMethods.GWL_EXSTYLE,
            ex | Native.NativeMethods.WS_EX_NOACTIVATE | Native.NativeMethods.WS_EX_TOOLWINDOW);
        if (controller != null) {
            controller.IsVisible = true;
            SyncWebViewBounds(form, controller);
        }
    }

    private void ApplyLoginMode() {
        if (_form.IsDisposed) {
            return;
        }
        _form.SuppressActivation = false;
        long ex = Native.NativeMethods.GetWindowLongPtrW(_form.Handle, Native.NativeMethods.GWL_EXSTYLE);
        Native.NativeMethods.SetWindowLongPtrW(_form.Handle, Native.NativeMethods.GWL_EXSTYLE,
            ex & ~Native.NativeMethods.WS_EX_NOACTIVATE & ~Native.NativeMethods.WS_EX_TOOLWINDOW);
        PlaceOnPrimaryMonitor();
        if (_controller != null) {
            _controller.IsVisible = true;
            SyncWebViewBounds();
        }
        _form.Show();
        _form.BringToFront();
        _form.Activate();
        _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        Log.Write("webview: login window shown");
    }

    private void PlaceOnPrimaryMonitor() => PlaceOnPrimaryMonitor(_form);

    private static void PlaceOnPrimaryMonitor(Form form) {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1200, 800);
        form.Location = new Point(
            area.Left + Math.Max(0, (area.Width - form.Width) / 2),
            area.Top + Math.Max(0, (area.Height - form.Height) / 2));
    }

    private void SyncWebViewBounds() => SyncWebViewBounds(_form, _controller);

    private static void SyncWebViewBounds(Form form, CoreWebView2Controller? controller) {
        if (controller == null || form.IsDisposed) {
            return;
        }
        var size = form.ClientSize;
        controller.Bounds = new Rectangle(0, 0, Math.Max(1, size.Width), Math.Max(1, size.Height));
        try {
            controller.NotifyParentWindowPositionChanged();
        } catch (Exception ex) {
            Log.Write("webview: position notify failed: " + ex.Message);
        }
    }

    private void OnHostFormResize(object? sender, EventArgs e) {
        if (_loginVisible && ReferenceEquals(sender, _form)) {
            SyncWebViewBounds();
        }
    }

    private void OnHostFormLocationChanged(object? sender, EventArgs e) {
        if (_loginVisible && ReferenceEquals(sender, _form)) {
            SyncWebViewBounds();
        }
    }

    private void OnHostFormClosing(object? sender, FormClosingEventArgs e) {
        // Only intercept the user's close button. Cancelling a WindowsShutDown /
        // TaskManagerClosing close blocks Windows from shutting down or signing out.
        if (_disposed || e.CloseReason != CloseReason.UserClosing) {
            return;
        }
        e.Cancel = true;
        if (ReferenceEquals(sender, _form)) {
            HideLoginWindow();
        }
    }

    /// <summary>True when the embedded browser is still on about:blank or some other non-chat host.</summary>
    private bool NeedsChatNavigation() {
        if (_webview == null) {
            return true;
        }
        try {
            string source = _webview.Source ?? "";
            if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? src) || string.IsNullOrEmpty(src.Host)) {
                return true;
            }
            string host = src.Host;
            foreach (string allowed in _site.StayOnHosts) {
                if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
            }
            return true;
        } catch {
            return true;
        }
    }

    private void OnFormActivated(object? sender, EventArgs e) {
        if (_loginVisible && ReferenceEquals(sender, _form)) {
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
            bool allowed = false;
            foreach (string micHost in _site.MicHosts) {
                if (host.Equals(micHost, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + micHost, StringComparison.OrdinalIgnoreCase)) {
                    allowed = true;
                    break;
                }
            }
            if (allowed && e.PermissionKind == CoreWebView2PermissionKind.Microphone) {
                e.State = CoreWebView2PermissionState.Allow;
                e.Handled = true;
                Log.Write("webview: mic permission granted for " + host);
            }
        } catch (Exception ex) {
            Log.Write("webview: permission handler error: " + ex.Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e) {
        Log.Write("webview: " + (IsStandby(sender) ? "standby " : "") + "navigate " + e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e) {
        if (IsStandby(sender)) {
            return; // WatchStandbyAsync tracks the standby page's readiness
        }
        _navCompletedGeneration = _navGeneration;
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
            Log.Write("webview: post-navigation probe, loggedIn=" + st.LoggedIn
                + ", canDictate=" + st.CanDictate);
            if (st.IsChatReady) {
                SetInteractive(true);
                LoginStateChanged?.Invoke(true);
                if (_loginVisible) {
                    HideLoginWindow();
                }
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
        _webview.NavigateToString(OfflinePage.HtmlFor(_site.DisplayName));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        if (!IsStandby(sender) && e.TryGetWebMessageAsString() == "retry") {
            Log.Write("webview: retry from offline page");
            RetryLoad();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) {
        if (IsStandby(sender)) {
            Log.Write($"webview: standby process failed ({e.ProcessFailedKind}) — dropping it");
            CloseStandby(); // recreated on the next ready load
            return;
        }
        Log.Write($"webview: process failed ({e.ProcessFailedKind}) — unloading");
        Unload(); // next hotkey press does a fresh EnsureReadyAsync
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) {
        Log.Write("webview: new window " + e.Uri);
        CoreWebView2Deferral deferral = e.GetDeferral();
        _ = OpenPopupAsync(e, deferral);
    }

    /// <summary>
    /// Google / Apple / Microsoft SSO open a popup. CoreWebView2Controller has no
    /// default popup UI, so an unhandled request is a black empty window. Create a
    /// fresh unused WebView in the same profile (required by NewWindow).
    /// </summary>
    private async Task OpenPopupAsync(CoreWebView2NewWindowRequestedEventArgs e, CoreWebView2Deferral deferral) {
        try {
            if (_env == null) {
                throw new InvalidOperationException("no WebView2 environment");
            }
            var popup = new Form {
                Text = "EchoType — Sign in",
                Size = new Size(720, 840),
                StartPosition = FormStartPosition.CenterScreen,
                ShowInTaskbar = true,
                BackColor = Color.White,
                MinimumSize = new Size(420, 520),
            };
            popup.Show();
            var controller = await _env.CreateCoreWebView2ControllerAsync(popup.Handle);
            controller.DefaultBackgroundColor = Color.White;
            controller.IsVisible = true;
            void SyncPopup() {
                controller.Bounds = new Rectangle(0, 0,
                    Math.Max(1, popup.ClientSize.Width), Math.Max(1, popup.ClientSize.Height));
                try { controller.NotifyParentWindowPositionChanged(); } catch { /* ignore */ }
            }
            SyncPopup();
            popup.Resize += (_, _) => SyncPopup();
            popup.LocationChanged += (_, _) => SyncPopup();
            ApplyChromeIdentity(controller.CoreWebView2);
            controller.CoreWebView2.WindowCloseRequested += (_, _) => {
                try { popup.Close(); } catch { /* ignore */ }
            };
            e.NewWindow = controller.CoreWebView2;
            e.Handled = true;
            popup.FormClosed += (_, _) => {
                try { controller.Close(); } catch { /* ignore */ }
                _popups.Remove(popup);
            };
            _popups.Add(popup);
            Log.Write("webview: SSO popup opened");
        } catch (Exception ex) {
            Log.Write("webview: popup failed (" + ex.Message + "), navigating in place");
            e.Handled = true;
            try {
                if (!string.IsNullOrEmpty(e.Uri)) {
                    _webview?.Navigate(e.Uri);
                }
            } catch (Exception navEx) {
                Log.Write("webview: in-place popup nav failed: " + navEx.Message);
            }
        } finally {
            deferral.Complete();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) {
        // Screen changes can leave the parked window on a removed monitor — re-park.
        if (_inited && !_loginVisible) {
            ApplyHiddenMode();
        }
        if (_standbyForm != null && _standbyController != null) {
            ApplyHiddenMode(_standbyForm, _standbyController);
        }
    }

    private void BeginPageLoad() {
        _navGeneration++;
        _loading = true;
        _showingOfflinePage = false;
        SetInteractive(false);
    }

    private void NavigateToChat() {
        BeginPageLoad();
        Log.Write("webview: loading " + _site.ChatUrl);
        _webview?.Navigate(_site.ChatUrl);
    }

    private void SetInteractive(bool value) {
        if (_interactive == value) {
            return;
        }
        _interactive = value;
        InteractiveChanged?.Invoke(value);
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
        _standbyForm?.Dispose();
    }

    /// <summary>Login host: title bar + close box. FormBorderStyle is fixed for the
    /// lifetime of the HWND — changing it would recreate the handle and detach WebView2.
    /// Hidden mode only moves the window offscreen; it does not hide the form.</summary>
    private sealed class WebViewHostForm : Form {
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool SuppressActivation { get; set; } = true;

        public WebViewHostForm() {
            Text = "EchoType — Sign in";
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            ControlBox = true;
            MinimizeBox = true;
            MaximizeBox = true;
            BackColor = Color.White;
            Size = new Size(WebViewWidth, WebViewHeight);
            MinimumSize = new Size(720, 560);
        }

        protected override bool ShowWithoutActivation => SuppressActivation;
    }
}
