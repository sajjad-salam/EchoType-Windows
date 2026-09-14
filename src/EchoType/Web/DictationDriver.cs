using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace EchoType.Web;

internal enum DriverFailure {
    NotReady,
    LoggedOut,
    ButtonNotFound,
    Timeout,
    Offline,
    JavaScript,
}

internal sealed class DriverException : Exception {
    public DriverFailure Kind { get; }

    public DriverException(DriverFailure kind, string detail) : base(detail) => Kind = kind;
}

/// <summary>Snapshot of the page state from window.__echotype.state().</summary>
internal sealed record PageState(
    bool LoggedIn,
    bool Dictating,
    bool ComposerPresent,
    string ComposerText,
    string Gum,           // getUserMedia outcome: none | requested | ok | err:<name>:<msg>
    string UserActivation, // none | active | had | unsupported
    string LastClick);

/// <summary>
/// JS bridge to chatgpt.com's dictation UI (DictationDriver.swift analog). All chatgpt.com
/// DOM specifics live in the injected <see cref="UserScriptSource"/> + <see cref="Selectors"/>.
///
/// Input synthesis ladder (the NSEvent-into-WKWebView equivalent): WebView2 has no
/// send-keys API and the hidden window must never take OS focus (that would break the
/// later paste into the user's app). CDP Input.dispatchKeyEvent/dispatchMouseEvent are
/// injected through Chromium's normal input pipeline as trusted events aimed at the
/// in-webview focused frame — no OS focus involved. The untrusted JS pointer sequence
/// (jsClick) stays as the last rung, mirroring the macOS version.
/// </summary>
internal sealed class DictationDriver {

    private readonly CoreWebView2 _webview;
    private readonly string _script;

    public DictationDriver(CoreWebView2 webview) {
        _webview = webview;
        _script = UserScriptSource;
    }

    // ------------------------------------------------------------------
    // Injected user script (port of DictationDriver.userScript; the DIAG-only
    // async forensics of the macOS version are dropped — they relied on
    // callAsyncJavaScript's await, which ExecuteScriptAsync can't do).
    // {SELECTORS} is replaced with Selectors.Js() at first use.
    // ------------------------------------------------------------------

    public static string UserScriptSource { get; } = UserScriptTemplate.Replace("{SELECTORS}", Selectors.Js());

    private const string UserScriptTemplate = """
        (function () {
          if (window.__echotype) return;
          const S = {SELECTORS};
          const E = {};
          // Record getUserMedia outcomes so mic failures are diagnosable from the app.
          if (navigator.mediaDevices && !navigator.mediaDevices.__etWrapped) {
            const orig = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
            navigator.mediaDevices.getUserMedia = (c) => {
              window.__etGUM = 'requested';
              return orig(c).then(s => { window.__etGUM = 'ok'; return s; })
                            .catch(e => { window.__etGUM = 'err:' + e.name + ':' + e.message; throw e; });
            };
            navigator.mediaDevices.__etWrapped = true;
          }
          // Record the last click the page actually received (position, trust,
          // target) — tells us whether synthesized clicks land correctly.
          if (!window.__etClickHooked) {
            document.addEventListener('click', (e) => {
              const btn = e.target && e.target.closest ? e.target.closest('button') : null;
              window.__etLastClick = {
                x: Math.round(e.clientX), y: Math.round(e.clientY),
                trusted: e.isTrusted,
                target: btn ? (btn.getAttribute('aria-label') || btn.getAttribute('data-testid') || 'button') : (e.target.tagName || '?')
              };
            }, true);
            window.__etClickHooked = true;
          }
          const buttons = () => [...document.querySelectorAll('button')];
          const label = (b) => (b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('data-testid') || '');
          const matches = (b, pattern) => new RegExp(pattern, 'i').test(label(b));
          const forbidden = (b) => S.neverClick.some(p => matches(b, p));
          E.findButton = (patterns) => {
            for (const p of patterns) {
              const hit = buttons().find(b => matches(b, p) && !forbidden(b));
              if (hit) return hit;
            }
            return null;
          };
          E.composer = () => {
            for (const sel of S.composer) {
              const el = document.querySelector(sel);
              if (el) return el;
            }
            return null;
          };
          // The composer leaves the DOM while dictation is active, so "logged in"
          // must accept the dictating state too.
          E.loggedIn = () => !document.querySelector(S.loggedOutMarker) && (!!E.composer() || E.isDictating());
          E.composerText = () => {
            const c = E.composer();
            return c ? c.innerText.replace(/\u200b/g, '').trim() : '';
          };
          E.clearComposer = () => {
            const c = E.composer();
            if (!c) return 'no-composer';
            c.focus();
            const sel = window.getSelection();
            const range = document.createRange();
            range.selectNodeContents(c);
            sel.removeAllRanges();
            sel.addRange(range);
            document.execCommand('delete');
            return 'ok';
          };
          E.isDictating = () => !!(E.findButton(S.submit) || E.findButton(S.cancel));
          E.start = () => {
            if (!E.loggedIn()) return 'logged-out';
            if (E.isDictating()) return 'ok';
            const b = E.findButton(S.start);
            if (!b) return 'no-start-button';
            b.click();
            return 'ok';
          };
          E.submit = () => {
            const b = E.findButton(S.submit);
            if (!b) return 'no-submit-button';
            b.click();
            return 'ok';
          };
          E.cancel = () => {
            const b = E.findButton(S.cancel);
            if (!b) return 'no-cancel-button';
            b.click();
            return 'ok';
          };
          E.state = () => JSON.stringify({
            loggedIn: E.loggedIn(),
            dictating: E.isDictating(),
            composerPresent: !!E.composer(),
            text: E.composerText(),
            gum: window.__etGUM || 'none',
            ua: navigator.userActivation
              ? (navigator.userActivation.isActive ? 'active' : (navigator.userActivation.hasBeenActive ? 'had' : 'none'))
              : 'unsupported',
            lastClick: window.__etLastClick || null
          });
          E.dump = () => JSON.stringify(
            buttons().map(b => ({ a: b.getAttribute('aria-label'), t: b.getAttribute('data-testid') }))
                     .filter(x => x.a || x.t)
          );
          // Full synthetic pointer/mouse sequence dispatched straight at the button.
          // Untrusted, but React handlers don't check isTrusted — they only need
          // the user activation a preceding trusted click already granted.
          E.jsClick = (kind) => {
            const b = E.findButton(S[kind]);
            if (!b) return 'null';
            const r = b.getBoundingClientRect();
            const opts = {
              bubbles: true, cancelable: true, composed: true, view: window,
              clientX: r.x + r.width / 2, clientY: r.y + r.height / 2,
              button: 0, buttons: 1, pointerId: 1, isPrimary: true, pointerType: 'mouse'
            };
            ['pointerover', 'pointerenter', 'pointermove'].forEach(t => b.dispatchEvent(new PointerEvent(t, opts)));
            b.dispatchEvent(new PointerEvent('pointerdown', opts));
            b.dispatchEvent(new MouseEvent('mousedown', opts));
            b.focus();
            b.dispatchEvent(new PointerEvent('pointerup', opts));
            b.dispatchEvent(new MouseEvent('mouseup', opts));
            b.dispatchEvent(new MouseEvent('click', opts));
            return 'ok';
          };
          E.dialogs = () => JSON.stringify(
            [...document.querySelectorAll('[role="dialog"], dialog')].map(d => (d.textContent || '').trim().slice(0, 150))
          );
          // Viewport-relative center of a dictation button, for click synthesis.
          E.centerOf = (kind) => {
            const b = E.findButton(S[kind]);
            if (!b) return 'null';
            b.scrollIntoView({ block: 'nearest' });
            const r = b.getBoundingClientRect();
            return JSON.stringify({ x: r.x + r.width / 2, y: r.y + r.height / 2 });
          };
          window.__echotype = E;
        })();
        """;

    // ------------------------------------------------------------------
    // JS evaluation
    // ------------------------------------------------------------------

    /// <summary>
    /// Re-prepends the user script before every call so __echotype survives reloads
    /// (mac 'call()' parity). ExecuteScriptAsync has no top-level return, so the
    /// expression is wrapped in an IIFE, and its JSON-encoded result is unwrapped.
    /// </summary>
    private async Task<string> EvalAsync(string expression) {
        string js = _script + "\n(function(){ return String(" + expression + "); })();";
        try {
            string encoded = await _webview.ExecuteScriptAsync(js);
            using var doc = JsonDocument.Parse(encoded);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString() ?? "undefined"
                : encoded;
        } catch (Exception ex) {
            throw new DriverException(DriverFailure.JavaScript, ex.Message);
        }
    }

    public async Task<PageState> StateAsync() {
        string json = await EvalAsync("__echotype.state()");
        if (json is "undefined" or "null") {
            throw new DriverException(DriverFailure.NotReady, $"state unavailable ({json})");
        }
        try {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string lastClick = "none";
            if (r.TryGetProperty("lastClick", out var lc) && lc.ValueKind == JsonValueKind.Object) {
                lastClick = "(" + Text(lc, "x") + "," + Text(lc, "y") + ")"
                    + " trusted=" + Text(lc, "trusted")
                    + " target=" + Text(lc, "target");
            }
            return new PageState(
                LoggedIn: r.GetProperty("loggedIn").GetBoolean(),
                Dictating: r.GetProperty("dictating").GetBoolean(),
                ComposerPresent: r.GetProperty("composerPresent").GetBoolean(),
                ComposerText: r.GetProperty("text").GetString() ?? "",
                Gum: r.GetProperty("gum").GetString() ?? "none",
                UserActivation: r.GetProperty("ua").GetString() ?? "?",
                LastClick: lastClick);
        } catch (DriverException) {
            throw;
        } catch (Exception ex) {
            throw new DriverException(DriverFailure.JavaScript, "bad state JSON: " + ex.Message);
        }
    }

    private static string Text(JsonElement obj, string name) {
        if (!obj.TryGetProperty(name, out var v)) return "?";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "?" : v.GetRawText();
    }

    // ------------------------------------------------------------------
    // CDP input synthesis
    // ------------------------------------------------------------------

    /// <summary>Ctrl+Shift+D toggles ChatGPT's dictation start/submit (mac sendDictationShortcut).</summary>
    private async Task SendDictationShortcutAsync() {
        await CdpKeyAsync(vk: 0x44, key: "D", code: "KeyD", modifiers: 10); // Ctrl=2 | Shift=8
        Log.Write("driver: sent Ctrl+Shift+D");
    }

    private async Task SendEscapeAsync() {
        await CdpKeyAsync(vk: 0x1B, key: "Escape", code: "Escape", modifiers: 0);
        Log.Write("driver: sent Esc");
    }

    private async Task CdpKeyAsync(uint vk, string key, string code, uint modifiers) {
        string down = FormattableString.Invariant($$"""
            {"type":"rawKeyDown","modifiers":{{modifiers}},"windowsVirtualKeyCode":{{vk}},"code":"{{code}}","key":"{{key}}","autoRepeat":false,"location":0}
            """);
        string up = FormattableString.Invariant($$"""
            {"type":"keyUp","modifiers":{{modifiers}},"windowsVirtualKeyCode":{{vk}},"code":"{{code}}","key":"{{key}}","autoRepeat":false,"location":0}
            """);
        try {
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", down));
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", up));
        } catch (DriverException) {
            throw;
        } catch (Exception ex) {
            throw new DriverException(DriverFailure.JavaScript, "CDP key dispatch failed: " + ex.Message);
        }
    }

    private async Task CdpClickAtAsync(double x, double y) {
        string press = FormattableString.Invariant($$"""
            {"type":"mousePressed","x":{{x}},"y":{{y}},"button":"left","buttons":1,"clickCount":1,"pointerType":"mouse"}
            """);
        string release = FormattableString.Invariant($$"""
            {"type":"mouseReleased","x":{{x}},"y":{{y}},"button":"left","buttons":0,"clickCount":1,"pointerType":"mouse"}
            """);
        try {
            // x,y are CSS pixels in the webview viewport — the same space as
            // getBoundingClientRect, so centerOf's output feeds straight in.
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", press));
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", release));
        } catch (DriverException) {
            throw;
        } catch (Exception ex) {
            throw new DriverException(DriverFailure.JavaScript, "CDP mouse dispatch failed: " + ex.Message);
        }
    }

    /// <summary>Clicks the dictation button of the given kind at its center.</summary>
    private async Task CdpClickButtonAsync(string kind) {
        string center = await EvalAsync($"__echotype.centerOf('{kind}')");
        if (center is "null" or "undefined") {
            await LogButtons("no-" + kind + "-button");
            throw new DriverException(DriverFailure.ButtonNotFound, "no-" + kind + "-button");
        }
        using var doc = JsonDocument.Parse(center);
        double x = doc.RootElement.GetProperty("x").GetDouble();
        double y = doc.RootElement.GetProperty("y").GetDouble();
        await CdpClickAtAsync(x, y);
        Log.Write($"driver: CDP click at css({x:F0},{y:F0}) ({kind})");
    }

    // CDP returns "" on success; a non-empty result is an error object.
    private static void CheckCdp(string result) {
        if (result.Length > 0) {
            throw new DriverException(DriverFailure.JavaScript, "CDP error: " + result);
        }
    }

    // ------------------------------------------------------------------
    // Start / submit / cancel
    // ------------------------------------------------------------------

    public async Task StartDictationAsync() {
        var st = await StateAsync();
        if (!st.LoggedIn) {
            throw new DriverException(DriverFailure.LoggedOut, "logged out");
        }
        if (st.Dictating) {
            return;
        }

        await ClearComposerAsync();

        // Rung 1: ChatGPT's own Ctrl+Shift+D shortcut.
        await SendDictationShortcutAsync();
        if (await AwaitEngagementAsync(TimeSpan.FromSeconds(1.5), forensics: false)) {
            return;
        }

        // Rung 2: trusted mouse click at the button's center.
        Log.Write("driver: shortcut didn't engage, retrying via CDP click");
        await CdpClickButtonAsync("start");
        if (await AwaitEngagementAsync(TimeSpan.FromSeconds(1.5), forensics: false)) {
            return;
        }

        // Rung 3: untrusted JS pointer sequence.
        Log.Write("driver: CDP click didn't engage, retrying via JS pointer events");
        _ = await EvalAsync("__echotype.jsClick('start')");
        await AwaitEngagementAsync(TimeSpan.FromSeconds(4), forensics: true); // throws on failure
    }

    /// <summary>Polls until dictating or the window closes. Throws on probe errors (mac parity).</summary>
    private async Task<bool> AwaitEngagementAsync(TimeSpan window, bool forensics) {
        DateTime deadline = DateTime.UtcNow + window;
        while (true) {
            var st = await StateAsync();
            if (st.Dictating) {
                return true;
            }
            if (DateTime.UtcNow > deadline) {
                if (forensics) {
                    await LogForensicsAsync(st);
                }
                throw new DriverException(DriverFailure.ButtonNotFound, $"mic didn't start: {st.Gum}");
            }
            await Task.Delay(200);
        }
    }

    public async Task SubmitDictationAsync() {
        await SendDictationShortcutAsync();
        if (!await PollWhileDictatingAsync(TimeSpan.FromSeconds(1.5))) {
            return; // stopped dictating — success
        }
        Log.Write("driver: shortcut didn't submit, falling back to CDP click");
        try {
            await CdpClickButtonAsync("submit");
        } catch (DriverException ex) when (ex.Kind == DriverFailure.ButtonNotFound) {
            // Submit button gone right after Ctrl+Shift+D = it landed; transcript follows.
            Log.Write("driver: submit button gone — treating as submitted");
        }
    }

    /// <summary>Polls until the dictation UI is gone; true = still dictating at the deadline.
    /// Probe failures count as "not dictating" (mac pollWhileDictating parity).</summary>
    private async Task<bool> PollWhileDictatingAsync(TimeSpan window) {
        DateTime deadline = DateTime.UtcNow + window;
        while (true) {
            bool dictating;
            try {
                dictating = (await StateAsync()).Dictating;
            } catch (DriverException ex) {
                Log.Write("driver: state probe failed while polling (" + ex.Message + ")");
                dictating = false;
            }
            if (!dictating) {
                return false;
            }
            if (DateTime.UtcNow > deadline) {
                return true;
            }
            await Task.Delay(150);
        }
    }

    public async Task CancelDictationAsync() {
        await SendEscapeAsync();
        if (!await PollWhileDictatingAsync(TimeSpan.FromSeconds(1.5))) {
            return;
        }
        Log.Write("driver: Esc didn't cancel, falling back to CDP click");
        try {
            await CdpClickButtonAsync("cancel");
        } catch (DriverException ex) {
            Log.Write("driver: cancel -> " + ex.Message);
        }
    }

    public async Task ClearComposerAsync() {
        try {
            _ = await EvalAsync("__echotype.clearComposer()");
        } catch (DriverException ex) {
            Log.Write("driver: clearComposer failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // Transcript
    // ------------------------------------------------------------------

    /// <summary>
    /// Polls until the transcript settles. No fixed cap (dictations run 30+ min);
    /// the deadline is an inactivity window that extends while the page shows progress.
    /// </summary>
    public async Task<string> AwaitTranscriptAsync(TimeSpan recordingDuration) {
        TimeSpan inactivityWindow = TimeSpan.FromSeconds(Math.Max(60, recordingDuration.TotalSeconds * 0.5));
        DateTime deadline = DateTime.UtcNow + inactivityWindow;
        string lastText = "";
        int stableCount = 0;
        int emptyGrace = 0;

        while (true) {
            var st = await StateAsync(); // failure → propagate (mac parity)
            if (DateTime.UtcNow > deadline) {
                throw new DriverException(DriverFailure.Timeout, "transcript never settled");
            }
            if (st.Dictating || !st.ComposerPresent) {
                deadline = DateTime.UtcNow + inactivityWindow;
            } else if (st.ComposerText.Length > 0 && st.ComposerText == lastText) {
                stableCount++;
                if (stableCount >= 2) {
                    return st.ComposerText;
                }
            } else if (st.ComposerText.Length == 0) {
                // Composer back and empty — an empty result is real after ~3 s.
                emptyGrace++;
                if (emptyGrace >= 12) {
                    return "";
                }
            } else {
                stableCount = 0;
                deadline = DateTime.UtcNow + inactivityWindow; // text still arriving
            }
            lastText = st.ComposerText;
            await Task.Delay(250);
        }
    }

    // ------------------------------------------------------------------
    // Forensics
    // ------------------------------------------------------------------

    private async Task LogForensicsAsync(PageState st) {
        Log.Write($"driver: dictation never engaged (gum={st.Gum} ua={st.UserActivation} lastClick={st.LastClick})");
        try {
            string logs = await EvalAsync("JSON.stringify(window.__etLogs || [])");
            Log.Write("driver: page console: " + Truncate(logs, 1500));
            string dialogs = await EvalAsync("__echotype.dialogs()");
            Log.Write("driver: dialogs on page: " + Truncate(dialogs, 500));
        } catch (DriverException ex) {
            Log.Write("driver: forensics failed: " + ex.Message);
        }
    }

    /// <summary>Logs every labelled button on the page so selector breakage is diagnosable.</summary>
    public async Task<string> DumpButtonsAsync() {
        try {
            return await EvalAsync("__echotype.dump()");
        } catch (DriverException ex) {
            return "dump failed: " + ex.Message;
        }
    }

    private async Task LogButtons(string context) {
        string dump = await DumpButtonsAsync();
        Log.Write($"driver: selector miss ({context}); buttons on page: {Truncate(dump, 2000)}");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
