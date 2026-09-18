using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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

internal sealed record AssistantImage(string Src, int Width, int Height, bool Loaded);

internal sealed record ModelReply(string Text, IReadOnlyList<AssistantImage> Images) {
    public static ModelReply Empty { get; } = new("", []);
    public bool IsEmpty => Text.Length == 0 && Images.Count == 0;
    public bool HasLoadedImage => Images.Any(i => i.Loaded || (i.Width >= 96 && i.Height >= 96));
}

/// <summary>
/// Conversation position captured before a prompt is sent, so a reused ChatGPT
/// thread can still detect the next turn when the new reply text matches the last one.
/// </summary>
internal readonly record struct CommandTurnSnapshot(
    ModelReply Reply,
    int UserTurns,
    int AssistantTurns,
    string AssistantKey) {
    public static CommandTurnSnapshot Empty { get; } = new(ModelReply.Empty, 0, 0, "");
}

/// <summary>Snapshot of the page state from window.__echotype.state().</summary>
internal sealed record PageState(
    bool LoggedIn,
    bool Dictating,
    bool ComposerPresent,
    bool CanDictate,
    bool LoginMarker,
    string ComposerText,
    string Gum,           // getUserMedia outcome: none | requested | ok | ended | err:<name>:<msg>
    string UserActivation, // none | active | had | unsupported
    string LastClick)
{
    /// <summary>Composer (or live dictation) is up and the mic control is on screen.</summary>
    public bool IsChatReady => LoggedIn && (CanDictate || Dictating);
}

/// <summary>Whether the model's page is still actually capturing audio.</summary>
internal sealed record CaptureState(
    bool IntendedListen,
    bool MicLive,
    bool UiDictating,
    bool Waveform,
    string Gum,
    string Composer,
    string LastUser);

/// <summary>
/// JS bridge to a chat site's dictation UI (DictationDriver.swift analog). DOM specifics
/// live in the injected user script plus a <see cref="SelectorSet"/> (ChatGPT or Gemini).
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
    private readonly SelectorSet _selectors;
    private readonly string _chatUrl;
    private readonly string _script;
    private bool _axEnabled;
    private bool _axUnavailable;
    private string _userTextBeforeDictation = "";
    private readonly HashSet<string> _axChromeBeforeDictation = new(StringComparer.Ordinal);
    private int _waitEpoch;

    /// <summary>
    /// Aborts in-flight transcript/reply waits so a cancelled session cannot stop
    /// the next prompt's generation or scrape the wrong bubble.
    /// </summary>
    public void InvalidateWaits() => Interlocked.Increment(ref _waitEpoch);

    /// <summary>
    /// ChatGPT/Gemini composers paint large inserts asynchronously. Sending before
    /// the input has settled can miss the Send button and look like a New Chat click.
    /// </summary>
    private const int LargeComposerChars = 2000;

    public DictationDriver(CoreWebView2 webview, SelectorSet selectors, string chatUrl = "") {
        _webview = webview;
        _selectors = selectors;
        _chatUrl = chatUrl;
        _script = MicCapture.HookScript + "\n" + UserScriptTemplate.Replace("{SELECTORS}", selectors.Js());
    }

    public string UserScript => _script;

    // ------------------------------------------------------------------
    // Injected user script (port of DictationDriver.userScript; the DIAG-only
    // async forensics of the macOS version are dropped — they relied on
    // callAsyncJavaScript's await, which ExecuteScriptAsync can't do).
    // {SELECTORS} is replaced with SelectorSet.Js() at first use.
    // ------------------------------------------------------------------

    private const string UserScriptTemplate = """
        (function () {
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
          const isVisible = (el) => {
            if (!el || !el.getBoundingClientRect) return false;
            const r = el.getBoundingClientRect();
            if (r.width < 4 || r.height < 4) return false;
            try {
              const st = getComputedStyle(el);
              if (st.display === 'none' || st.visibility === 'hidden' || Number(st.opacity) === 0) return false;
            } catch (e) {}
            return true;
          };
          const deepQueryAll = (sel, root) => {
            const out = [];
            const visit = (node) => {
              if (!node) return;
              try { out.push(...node.querySelectorAll(sel)); } catch (e) {}
              const all = node.querySelectorAll ? node.querySelectorAll('*') : [];
              for (const el of all) {
                if (el.shadowRoot) visit(el.shadowRoot);
                if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
                  try { if (el.contentDocument) visit(el.contentDocument); } catch (e) {}
                }
              }
            };
            visit(root || document);
            return out;
          };
          const deepQuery = (sel, root) => {
            const hits = deepQueryAll(sel, root);
            return hits.find(isVisible) || hits[0] || null;
          };
          const buttons = () => {
            const seen = new Set();
            const out = [];
            for (const el of deepQueryAll('button, [role="button"], a[aria-label], a[data-test-id], a[data-testid]')) {
              if (seen.has(el)) continue;
              seen.add(el);
              out.push(el);
            }
            return out;
          };
          const label = (b) => (b.getAttribute('aria-label') || '') + ' ' + (b.getAttribute('data-testid') || '') + ' ' + (b.getAttribute('data-test-id') || '');
          const matches = (b, pattern) => {
            const re = new RegExp(pattern, 'i');
            if (re.test(label(b))) return true;
            const text = String(b.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 80);
            return !!text && re.test(text);
          };
          const forbidden = (b) => S.neverClick.some(p => matches(b, p));
          E.findButton = (patterns, allowForbidden) => {
            if (!patterns) return null;
            const all = buttons();
            for (const p of patterns) {
              const hit = all.find(b => matches(b, p) && isVisible(b) && (allowForbidden || !forbidden(b)));
              if (hit) return hit;
            }
            return null;
          };
          E.findCss = (sels) => {
            if (!sels) return null;
            for (const sel of sels) {
              const el = deepQuery(sel);
              if (el && isVisible(el)) return el;
            }
            return null;
          };
          E.nearbySend = () => {
            const c = E.composer();
            if (!c) return null;
            let root = c.closest('form') || c.parentElement;
            for (let i = 0; i < 8 && root; i++) {
              const btns = deepQueryAll('button, [role="button"]', root);
              for (let j = btns.length - 1; j >= 0; j--) {
                const b = btns[j];
                if (!isVisible(b) || b.disabled || b.getAttribute('aria-disabled') === 'true') continue;
                const lab = label(b);
                if (/mic|listen|voice|استماع|ميكروفون/i.test(lab)) continue;
                if (/send|submit|إرسال|ارسال/i.test(lab) || /send/.test(b.className || '') ||
                    b.getAttribute('data-test-id') === 'send-button' || b.getAttribute('data-testid') === 'send-button') {
                  return b;
                }
              }
              root = root.parentElement;
            }
            return null;
          };
          E.button = (kind) => {
            const allow = kind === 'send' || kind === 'stop' || kind === 'newChat' || kind === 'start' || kind === 'submit';
            const found = E.findCss(S[kind + 'Css']) || E.findButton(S[kind], allow);
            if (found) return found;
            if (kind === 'send') {
              const near = E.nearbySend();
              if (near) return near;
            }
            // Gemini: the mic button is also the stop control while listening.
            // ChatGPT must not use this — after the overlay closes it restarts dictation.
            if (S.submitFallsBackToStart && (kind === 'submit' || kind === 'cancel')) {
              return E.findCss(S.startCss) || E.findButton(S.start, true);
            }
            return null;
          };
          E.cleanText = (s) => String(s || '').replace(/[\u200b\u200c\u200d\ufeff]/g, '').replace(/\s+/g, ' ').trim();
          E.isChromeText = (t) => /^(ask anything|ask chatgpt|ask gemini|message chatgpt|enter a prompt for gemini|enter a prompt|what'?s on the agenda today\??|how can i help you today\??|type a message|think|search|study|voice|send|attach|new chat|chatgpt|gemini|اكتب رسالة|اسأل أي شيء)$/i.test(t || '');
          E.elementText = (el) => {
            if (!el) return '';
            const take = (s, best) => { s = E.cleanText(s); return s.length > best.length ? s : best; };
            let best = '';
            if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') return E.cleanText(el.value);
            best = take(el.value, best);
            best = take(el.textContent, best);
            best = take(el.innerText, best);
            try {
              const view = el.pmView;
              if (view && view.state && view.state.doc) best = take(view.state.doc.textContent, best);
            } catch (e) {}
            try {
              if (el.shadowRoot) {
                best = take(el.shadowRoot.textContent, best);
                best = take(el.shadowRoot.innerText, best);
              }
            } catch (e) {}
            if (el.querySelectorAll) {
              for (const f of el.querySelectorAll('textarea, input')) best = take(f.value, best);
            }
            if (!best && el.classList && el.classList.contains('ql-blank')) return '';
            return E.isChromeText(best) ? '' : best;
          };
          E.harvestVisibleText = (root) => {
            if (!root) return '';
            const skipSel = 'button,svg,nav,aside,style,script,noscript,[aria-hidden="true"]';
            let s = '';
            const walk = (node) => {
              if (!node) return;
              if (node.nodeType === 3) { s += node.nodeValue || ''; return; }
              if (node.nodeType !== 1) return;
              if (node.matches && node.matches(skipSel)) return;
              if (node.tagName === 'TEXTAREA' || node.tagName === 'INPUT') { s += ' ' + (node.value || ''); return; }
              if (node.shadowRoot) walk(node.shadowRoot);
              for (const ch of node.childNodes) walk(ch);
            };
            walk(root);
            let t = E.cleanText(s);
            t = t.replace(/^(What'?s on the agenda today\??|How can I help you today\??)\s*/i, '');
            t = t.replace(/\s+(Think|Search|Study|Voice|Send|Attach|Reason|تفكير|بحث|إرسال)$/i, '').trim();
            return E.isChromeText(t) ? '' : t;
          };
          E.nearbyComposerText = () => {
            const c = E.composer();
            if (!c) return '';
            const roots = [];
            const form = c.closest('form');
            if (form) roots.push(form);
            if (c.parentElement && c.parentElement !== form) roots.push(c.parentElement);
            const extra = (form || c).parentElement;
            if (extra && roots.indexOf(extra) < 0) {
              const hasTurns = extra.querySelector && extra.querySelector('[data-message-author-role], [data-turn], article, user-query, model-response');
              if (!hasTurns) roots.push(extra);
            }
              let best = '';
              for (const root of roots) {
                const t = E.harvestVisibleText(root);
                if (t.length > best.length && t.length < 500000) best = t;
              }
            return best;
          };
          E.inSideChrome = (el) => {
            try {
              return !!(el.closest && el.closest('nav, aside, [role="navigation"], [role="complementary"], [data-test-id="conversation-list"]'));
            } catch (e) { return false; }
          };
          E.composerPick = (list) => {
            const vis = [];
            for (const el of list) {
              if (!el || !el.getBoundingClientRect) continue;
              const r = el.getBoundingClientRect();
              if (r.width < 40 || r.height < 12 || r.height > 400) continue;
              const lab = ((el.getAttribute('aria-label') || '') + ' ' +
                (el.getAttribute('placeholder') || '') + ' ' +
                (el.getAttribute('data-placeholder') || '') + ' ' +
                (el.id || '') + ' ' + (el.className || '')).toLowerCase();
              const promptish = /ql-editor|prompt-textarea|prompt|message chatgpt|ask anything|ask gemini|gemini|composer|prosemirror|rich-textarea/.test(lab)
                || !!(el.closest && el.closest('rich-textarea, #prompt-textarea'));
              vis.push({ el, top: r.top, promptish });
            }
            vis.sort((a, b) => {
              if (a.promptish !== b.promptish) return a.promptish ? -1 : 1;
              return b.top - a.top;
            });
            return vis.length ? vis[0].el : null;
          };
          E.composer = () => {
            const seen = new Set();
            const hits = [];
            for (const sel of S.composer) {
              for (const el of deepQueryAll(sel)) {
                if (seen.has(el) || E.inSideChrome(el)) continue;
                seen.add(el);
                hits.push(el);
              }
            }
            let best = E.composerPick(hits);
            if (best) return best;
            const minTop = (window.innerHeight || 0) * 0.4;
            const extra = [];
            for (const el of deepQueryAll('[contenteditable="true"], [contenteditable="plaintext-only"], textarea, [role="textbox"]')) {
              if (seen.has(el) || E.inSideChrome(el)) continue;
              const r = el.getBoundingClientRect();
              if (r.width < 40 || r.height < 12 || r.height > 280 || r.top < minTop) continue;
              extra.push(el);
            }
            return E.composerPick(extra) || hits[0] || null;
          };
          // The composer leaves the DOM while dictation is active, so "logged in"
          // must accept the dictating state too.
          E.loggedIn = () => !document.querySelector(S.loggedOutMarker) && (!!E.composer() || E.isDictating());
          E.queryAll = (sels) => {
            const out = [];
            const seen = new Set();
            if (!sels) return out;
            for (const sel of sels) {
              for (const el of deepQueryAll(sel)) {
                if (seen.has(el)) continue;
                seen.add(el);
                out.push(el);
              }
            }
            return out;
          };
          E.messageNodes = (sels) => {
            const nodes = E.queryAll(sels);
            return nodes.filter(n => !nodes.some(o => o !== n && o.contains(n)));
          };
          E.messageText = (el) => {
            if (!el) return '';
            let md = null;
            if (S.assistantMarkdown) {
              for (const sel of S.assistantMarkdown) {
                try { md = el.querySelector(sel); if (md && E.elementText(md)) break; else md = null; } catch (e) {}
              }
            }
            const target = md || el;
            try {
              const converted = E.toMarkdown(target).replace(/[\u200b\u200c\u200d\ufeff]/g, '').trim();
              if (converted) return converted;
            } catch (e) {}
            return E.elementText(target);
          };
          E.isConversationTitle = (t) => {
            t = E.cleanText(t);
            if (!t || t.length < 10) return false;
            const sels = [
              'conversations-list a', 'conversations-list button',
              '[data-test-id="conversation"]', '[data-test-id="conversation-title"]',
              'nav a', 'aside a', 'aside button',
              '[role="navigation"] a', '[role="navigation"] button'
            ];
            for (const sel of sels) {
              for (const el of deepQueryAll(sel)) {
                if (E.cleanText(el.textContent || el.getAttribute('aria-label') || '') === t) return true;
              }
            }
            return false;
          };
          E.composerText = () => {
            const c = E.composer();
            let t = E.elementText(c);
            if (!t) t = E.nearbyComposerText();
            if (t && E.isConversationTitle(t)) return '';
            return t;
          };
          E.composerDebug = () => {
            const c = E.composer();
            if (!c) return JSON.stringify({ none: true, iframes: document.querySelectorAll('iframe').length });
            return JSON.stringify({
              tag: c.tagName,
              id: c.id || '',
              cls: String(c.className || '').slice(0, 160),
              ce: c.getAttribute('contenteditable') || '',
              role: c.getAttribute('role') || '',
              html: String(c.innerHTML || '').slice(0, 400),
              inner: String(c.innerText || '').slice(0, 160),
              text: String(c.textContent || '').slice(0, 160),
              value: String(c.value || '').slice(0, 160),
              shadow: !!c.shadowRoot,
              iframes: document.querySelectorAll('iframe').length,
              nearby: (E.nearbyComposerText() || '').slice(0, 160)
            });
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
            try { c.textContent = ''; } catch (e) {}
            if (c.classList) c.classList.add('ql-blank');
            return 'ok';
          };
          E.focusComposer = () => {
            const c = E.composer();
            if (!c) return 'no-composer';
            c.focus();
            return 'ok';
          };
          E.setComposer = (text) => {
            const c = E.composer();
            if (!c) return 'no-composer';
            c.focus();
            const sel = window.getSelection();
            const range = document.createRange();
            range.selectNodeContents(c);
            sel.removeAllRanges();
            sel.addRange(range);
            document.execCommand('delete');
            const ok = document.execCommand('insertText', false, text);
            if (!ok || !E.composerText()) {
              try {
                const dt = new DataTransfer();
                dt.setData('text/plain', text);
                c.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
              } catch (e) {
                c.textContent = text;
                c.dispatchEvent(new InputEvent('input', { bubbles: true, data: text, inputType: 'insertText' }));
              }
            }
            if (c.classList) c.classList.remove('ql-blank');
            return E.composerText() ? 'ok' : 'empty';
          };
          E.pokeComposer = () => {
            const c = E.composer();
            if (!c) return 'no-composer';
            try {
              c.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertText', data: E.elementText(c) }));
              c.dispatchEvent(new Event('change', { bubbles: true }));
            } catch (e) {}
            return 'ok';
          };
          E.sendReady = () => {
            const b = E.button('send');
            if (!b) return 'missing';
            if (b.disabled || b.getAttribute('aria-disabled') === 'true') return 'disabled';
            return 'ok';
          };
          E.newChat = () => {
            const b = E.button('newChat');
            if (!b) return 'no-new-chat';
            b.click();
            return 'ok';
          };
          // Reconstruct GFM from ChatGPT's rendered HTML so fences, headings,
          // emphasis, lists, and tables survive the paste (innerText strips them).
          E.toMarkdown = (root) => {
            const skipSel = 'button,svg,style,script,noscript,[aria-hidden="true"]';
            const langOf = (code, block) => {
              const cls = ((code && code.className) || '') + '';
              const m = cls.match(/language-([a-zA-Z0-9+#._-]+)/);
              if (m) return m[1];
              if (!block || !block.querySelectorAll) return '';
              for (const el of block.querySelectorAll('div, span')) {
                if (code && (el === code || el.contains(code) || code.contains(el))) continue;
                if (el.querySelector && el.querySelector('button, code')) continue;
                const t = (el.textContent || '').trim();
                if (t && t.length < 24 && !/\s/.test(t) && !/^copy/i.test(t)) return t;
              }
              return '';
            };
            const fence = (lang, body) => {
              body = String(body || '').replace(/\u200b/g, '').replace(/\n+$/, '');
              let n = 3;
              const ticks = body.match(/`+/g);
              if (ticks) for (const t of ticks) if (t.length >= n) n = t.length + 1;
              const f = '`'.repeat(n);
              return f + (lang || '') + '\n' + body + '\n' + f;
            };
            const isCodeWrap = (el) => {
              if (!el || el.nodeType !== 1) return false;
              if (el.tagName === 'PRE') return true;
              if (!el.querySelector) return false;
              if (el.querySelector('p,h1,h2,h3,h4,h5,h6,ul,ol,li,table,blockquote')) return false;
              const codes = el.querySelectorAll('code');
              if (codes.length !== 1) return false;
              const code = codes[0];
              return !!(el.querySelector('button') || /whitespace-pre|hljs|language-/.test(code.className || ''));
            };
            const walk = (node) => {
              if (!node) return '';
              if (node.nodeType === 3) return (node.nodeValue || '').replace(/\u200b/g, '').replace(/\u00a0/g, ' ');
              if (node.nodeType !== 1) return '';
              if (node.matches && node.matches(skipSel)) return '';
              const tag = node.tagName.toLowerCase();
              if (tag === 'br') return '\n';
              if (tag === 'hr') return '\n\n---\n\n';
              if (isCodeWrap(node)) {
                const code = node.querySelector('code') || node;
                return '\n\n' + fence(langOf(code === node ? null : code, node), code.innerText || '') + '\n\n';
              }
              if (tag === 'code') {
                const t = (node.textContent || '').replace(/\u200b/g, '');
                if (!t) return '';
                const n = t.indexOf('`') >= 0 ? 2 : 1;
                const b = '`'.repeat(n);
                return n > 1 ? b + ' ' + t + ' ' + b : b + t + b;
              }
              if (/^h[1-6]$/.test(tag)) return '\n\n' + '#'.repeat(+tag[1]) + ' ' + kids(node).trim() + '\n\n';
              if (tag === 'strong' || tag === 'b') return '**' + kids(node) + '**';
              if (tag === 'em' || tag === 'i') return '*' + kids(node) + '*';
              if (tag === 'del' || tag === 's') return '~~' + kids(node) + '~~';
              if (tag === 'a') {
                const href = node.getAttribute('href') || '';
                const text = kids(node);
                return href && href.indexOf('javascript:') !== 0 ? '[' + text + '](' + href + ')' : text;
              }
              if (tag === 'img') {
                const src = node.getAttribute('src') || '';
                const alt = node.getAttribute('alt') || '';
                return src ? '![' + alt + '](' + src + ')' : alt;
              }
              if (tag === 'blockquote') {
                return '\n\n' + kids(node).trim().split('\n').map(function (l) { return '> ' + l; }).join('\n') + '\n\n';
              }
              if (tag === 'ul' || tag === 'ol') {
                let i = tag === 'ol' ? (parseInt(node.getAttribute('start') || '1', 10) || 1) : 0;
                let out = '\n\n';
                for (const li of node.children) {
                  if (!li.tagName || li.tagName.toLowerCase() !== 'li') continue;
                  const bullet = tag === 'ol' ? (i++) + '. ' : '- ';
                  out += bullet + kids(li).trim().replace(/\n/g, '\n  ') + '\n';
                }
                return out + '\n';
              }
              if (tag === 'table') {
                const rows = [...node.querySelectorAll('tr')].map(function (tr) {
                  return [...tr.querySelectorAll('th,td')].map(function (c) {
                    return kids(c).trim().replace(/\|/g, '\\|');
                  });
                }).filter(function (r) { return r.length; });
                if (!rows.length) return '';
                const fmt = function (r) { return '| ' + r.join(' | ') + ' |'; };
                const sep = '| ' + rows[0].map(function () { return '---'; }).join(' | ') + ' |';
                return '\n\n' + [fmt(rows[0]), sep].concat(rows.slice(1).map(fmt)).join('\n') + '\n\n';
              }
              if (tag === 'p') return '\n\n' + kids(node).trim() + '\n\n';
              return kids(node);
            };
            const kids = (node) => {
              let s = '';
              for (const c of node.childNodes) s += walk(c);
              return s;
            };
            return kids(root).replace(/\n{3,}/g, '\n\n').trim();
          };
          E.collectImages = (root) => {
            const out = [];
            const seen = new Set();
            const skipSrc = (src) => {
              if (!src) return true;
              const s = String(src);
              if (s.indexOf('data:image/svg') === 0) return true;
              if (/avatar|profile[\/_-]|\/icon|favicon|emoji|spinner|logo\.|s\.gravatar/i.test(s)) return true;
              return false;
            };
            const addImg = (img) => {
              if (!img) return;
              const src = img.currentSrc || img.getAttribute('src') || '';
              if (skipSrc(src) || seen.has(src)) return;
              const w = img.naturalWidth || img.width || 0;
              const h = img.naturalHeight || img.height || 0;
              if ((w && w < 96) || (h && h < 96)) return;
              seen.add(src);
              out.push({ src: src, width: w, height: h, loaded: !!(img.complete && w >= 96 && h >= 96) });
            };
            const visit = (node) => {
              if (!node) return;
              if (node.nodeType === 9) { visit(node.documentElement); return; }
              if (node.nodeType !== 1) return;
              if (node.tagName === 'IMG') addImg(node);
              if (node.tagName === 'IFRAME' || node.tagName === 'FRAME') {
                try { if (node.contentDocument) visit(node.contentDocument); } catch (e) {}
              }
              try { if (node.shadowRoot) visit(node.shadowRoot); } catch (e) {}
              if (!node.querySelectorAll) return;
              for (const img of node.querySelectorAll('img')) addImg(img);
              for (const el of node.querySelectorAll('*')) {
                try { if (el.shadowRoot) visit(el.shadowRoot); } catch (e) {}
                if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
                  try { if (el.contentDocument) visit(el.contentDocument); } catch (e) {}
                }
              }
            };
            visit(root);
            try {
              const t = E.messageText(root);
              const re = /!\[[^\]]*\]\(([^)\s]+)\)/g;
              let m;
              while ((m = re.exec(t))) {
                const src = m[1];
                if (skipSrc(src) || seen.has(src)) continue;
                if (/^https?:|^blob:|^data:image\//i.test(src)) {
                  seen.add(src);
                  out.push({ src: src, width: 0, height: 0, loaded: false });
                }
              }
            } catch (e) {}
            return out;
          };
          // ChatGPT/Gemini image gen posts a short status line first ("Generating
          // your image..." / "جارٍ إنشاء صورتك") and re-enables Send. That is not
          // the finished reply — keep waiting until a real image lands.
          E.isImagePlaceholder = (t) => {
            t = E.cleanText(t);
            if (!t) return false;
            // Status lines are short. Long replies that mention images
            // (translations, app docs) must not look like image generation.
            if (t.length > 160) return false;
            return /^(generating.{0,40}image|creating.{0,40}image|working on.{0,24}image|image.{0,24}(generat|creat)|جار[يٍ]?\s*إنشاء.{0,20}صور|توليد.{0,20}صور|إنشاء صور|generando.{0,24}imagen|g[eé]n[eé]ration.{0,24}image|正在生成.{0,12}图)/i.test(t);
          };
          E.nodeLooksBusy = (node) => {
            if (!node) return false;
            try {
              if (node.getAttribute && (node.getAttribute('aria-busy') === 'true' || node.getAttribute('data-is-loading') === 'true')) return true;
              if (node.querySelector && node.querySelector('[role="progressbar"], [aria-busy="true"], progress, [data-testid*="progress"], [class*="progress-bar"]')) return true;
            } catch (e) {}
            return E.isImagePlaceholder(E.elementText(node));
          };
          E.lastAssistantNode = () => {
            const nodes = E.messageNodes(S.assistant);
            for (let i = nodes.length - 1; i >= 0; i--) {
              if (E.messageText(nodes[i])) return nodes[i];
              if (E.collectImages(nodes[i]).length) return nodes[i];
              if (E.nodeLooksBusy(nodes[i])) return nodes[i];
            }
            return null;
          };
          E.assistantTurnKey = () => {
            const nodes = E.messageNodes(S.assistant);
            const n = E.lastAssistantNode();
            if (!n) return '';
            let idx = -1;
            for (let i = 0; i < nodes.length; i++) {
              if (nodes[i] === n) { idx = i; break; }
            }
            const id = (n.getAttribute('data-message-id') || n.id || '').slice(0, 80);
            return idx + ':' + id;
          };
          E.imagePending = () => {
            const node = E.lastAssistantNode();
            if (!node) return false;
            const images = E.collectImages(node);
            if (images.some(function (i) { return i.loaded; })) return false;
            const text = E.messageText(node) || E.elementText(node);
            return E.isImagePlaceholder(text) || E.nodeLooksBusy(node);
          };
          E.lastAssistantText = () => {
            const n = E.lastAssistantNode();
            return n ? E.messageText(n) : '';
          };
          E.lastAssistantImages = () => E.collectImages(E.lastAssistantNode());
          E.encodeBestAssistantImage = () => {
            const node = E.lastAssistantNode();
            if (!node) return JSON.stringify({ status: 'missing' });
            let best = null, bestArea = 0;
            const consider = (img) => {
              const w = img.naturalWidth || 0, h = img.naturalHeight || 0;
              if (w < 96 || h < 96) return;
              const area = w * h;
              if (area > bestArea) { best = img; bestArea = area; }
            };
            const visit = (n) => {
              if (!n) return;
              if (n.nodeType === 9) { visit(n.documentElement); return; }
              if (n.nodeType !== 1) return;
              if (n.tagName === 'IMG') consider(n);
              if (n.tagName === 'IFRAME' || n.tagName === 'FRAME') {
                try { if (n.contentDocument) visit(n.contentDocument); } catch (e) {}
              }
              try { if (n.shadowRoot) visit(n.shadowRoot); } catch (e) {}
              if (!n.querySelectorAll) return;
              for (const img of n.querySelectorAll('img')) consider(img);
              for (const el of n.querySelectorAll('*')) {
                try { if (el.shadowRoot) visit(el.shadowRoot); } catch (e) {}
                if (el.tagName === 'IFRAME' || el.tagName === 'FRAME') {
                  try { if (el.contentDocument) visit(el.contentDocument); } catch (e) {}
                }
              }
            };
            visit(node);
            if (!best) return JSON.stringify({ status: 'none' });
            const src = best.currentSrc || best.src || '';
            try {
              const c = document.createElement('canvas');
              c.width = best.naturalWidth;
              c.height = best.naturalHeight;
              c.getContext('2d').drawImage(best, 0, 0);
              return JSON.stringify({ status: 'ok', data: c.toDataURL('image/png'), src: src });
            } catch (e) {
              return JSON.stringify({ status: 'tainted', src: src, error: String((e && e.message) || e) });
            }
          };
          E.beginFetchImage = (src) => {
            window.__etImg = { status: 'pending', data: null, error: null };
            fetch(src, { credentials: 'include' }).then(function (r) {
              if (!r.ok) throw new Error('http ' + r.status);
              return r.blob();
            }).then(function (blob) {
              return new Promise(function (resolve, reject) {
                const fr = new FileReader();
                fr.onload = function () { resolve(fr.result); };
                fr.onerror = reject;
                fr.readAsDataURL(blob);
              });
            }).then(function (data) {
              window.__etImg = { status: 'ok', data: data };
            }).catch(function (e) {
              window.__etImg = { status: 'err', error: String((e && e.message) || e) };
            });
            return 'ok';
          };
          E.fetchImageStatus = () => {
            const s = window.__etImg || { status: 'none' };
            if (s.status === 'ok') return JSON.stringify({ status: 'ok', data: s.data || '' });
            return JSON.stringify({ status: s.status || 'none', error: s.error || '' });
          };
          E.lastUserText = () => {
            const nodes = E.messageNodes(S.user);
            for (let i = nodes.length - 1; i >= 0; i--) {
              const t = E.elementText(nodes[i]);
              if (t) return t;
            }
            return '';
          };
          // Empty thread = no user/assistant bubbles. Composer-empty is not enough:
          // Gemini's input is blank after a reply, so "new chat" can look successful
          // while the same conversation is still on screen.
          E.conversationFresh = () => (E.lastUserText() || E.lastAssistantText() || E.lastAssistantImages().length) ? 'false' : 'true';
          // Stop / .result-streaming cover token streaming. Image generation does
          // not disable Send, so it is reported separately as imagePending.
          E.isGenerating = () => !!(E.button('stop') || E.findCss(S.generatingCss)
            || document.querySelector('.result-streaming, [data-testid="stop-button"]'));
          E.replyState = () => JSON.stringify({
            generating: E.isGenerating(),
            imagePending: !!E.imagePending(),
            composer: E.composerText(),
            last: E.lastAssistantText(),
            user: E.lastUserText(),
            userTurns: E.messageNodes(S.user).length,
            assistantTurns: E.messageNodes(S.assistant).length,
            assistantKey: E.assistantTurnKey(),
            images: E.lastAssistantImages()
          });
          E.micListening = () => {
            const b = E.findCss(S.startCss) || E.findButton(S.start, true);
            if (!b) return false;
            const pressed = b.getAttribute('aria-pressed') === 'true'
              || b.getAttribute('aria-checked') === 'true';
            const lab = label(b);
            return pressed || /stop (listening|recording|voice)|listening|إيقاف/i.test(lab);
          };
          // Real dictation UI, ignoring the __etListen flag we set ourselves.
          E.uiDictating = () => !!(
            E.findCss(S.dictatingCss)
            || E.findButton(S.submit)
            || E.findButton(S.cancel)
            || E.micListening()
          );
          E.waveformPresent = () => {
            if (E.findCss(S.dictatingCss)) return true;
            const look = (sel) => {
              try { return deepQueryAll(sel); } catch (e) { return []; }
            };
            const hits = [
              ...look('speech-dictation-mic-button[listening]'),
              ...look('speech-dictation-mic-button[recording]'),
              ...look('[class*="waveform"]'),
              ...look('[class*="voice-visual"]'),
              ...look('[class*="speech-visual"]'),
              ...look('canvas'),
              ...look('svg')
            ];
            const mic = E.findCss(S.startCss) || E.findButton(S.start, true);
            const mr = mic ? mic.getBoundingClientRect() : null;
            for (const el of hits) {
              if (!isVisible(el)) continue;
              const r = el.getBoundingClientRect();
              const tag = (el.tagName || '').toLowerCase();
              const cls = String(el.getAttribute('class') || '') + ' ' + (el.getAttribute('aria-label') || '');
              if (/wave|voice|speech|dictat|visual|audio|mic/i.test(cls)) return true;
              if (tag === 'canvas' || tag === 'svg') {
                if (r.width < 32 || r.height < 8) continue;
                if (mr) {
                  const near = Math.abs((r.x + r.width / 2) - (mr.x + mr.width / 2)) < 320
                    && Math.abs((r.y + r.height / 2) - (mr.y + mr.height / 2)) < 180;
                  if (near && r.width >= 40) return true;
                }
                continue;
              }
              return true;
            }
            return false;
          };
          E.micTracksLive = () => {
            try {
              if (typeof window.__etRefreshMic === 'function') window.__etRefreshMic();
            } catch (e) {}
            if (window.__etMicLive) return true;
            try {
              const frames = document.querySelectorAll('iframe');
              for (const f of frames) {
                try {
                  const w = f.contentWindow;
                  if (!w) continue;
                  if (typeof w.__etRefreshMic === 'function') w.__etRefreshMic();
                  if (w.__etMicLive) return true;
                } catch (e) {}
              }
            } catch (e) {}
            return false;
          };
          E.isDictating = () => !!(
            E.uiDictating()
            || (S.treatGumAsEngaged && window.__etListen)
          );
          E.captureState = () => JSON.stringify({
            listen: !!window.__etListen,
            micLive: E.micTracksLive(),
            ui: E.uiDictating(),
            wave: E.waveformPresent(),
            gum: window.__etGUM || 'none',
            composer: E.composerText(),
            user: E.lastUserText()
          });
          E.start = () => {
            if (!E.loggedIn()) return 'logged-out';
            if (E.isDictating()) return 'ok';
            const b = E.findCss(S.startCss) || E.findButton(S.start);
            if (!b) return 'no-start-button';
            window.__etListen = true;
            b.click();
            return 'ok';
          };
          E.submit = () => {
            const b = E.findCss(S.submitCss) || E.findButton(S.submit)
              || E.findCss(S.startCss) || E.findButton(S.start, true);
            if (!b) return 'no-submit-button';
            window.__etListen = false;
            b.click();
            return 'ok';
          };
          E.cancel = () => {
            const b = E.findCss(S.cancelCss) || E.findButton(S.cancel);
            window.__etListen = false;
            if (!b) return 'no-cancel-button';
            b.click();
            return 'ok';
          };
          E.markListening = (on) => { window.__etListen = !!on; return 'ok'; };
          E.state = () => JSON.stringify({
            loggedIn: E.loggedIn(),
            dictating: E.isDictating(),
            composerPresent: !!E.composer(),
            canDictate: !!(E.findCss(S.startCss) || E.findButton(S.start, true) || E.isDictating()),
            loginMarker: !!document.querySelector(S.loggedOutMarker),
            text: E.composerText(),
            gum: window.__etGUM || 'none',
            ua: navigator.userActivation
              ? (navigator.userActivation.isActive ? 'active' : (navigator.userActivation.hasBeenActive ? 'had' : 'none'))
              : 'unsupported',
            lastClick: window.__etLastClick || null
          });
          E.dump = () => JSON.stringify(
            buttons().map(b => ({ a: b.getAttribute('aria-label'), t: b.getAttribute('data-testid') || b.getAttribute('data-test-id') }))
                     .filter(x => x.a || x.t)
          );
          // Full synthetic pointer/mouse sequence dispatched straight at the button.
          // Untrusted, but React handlers don't check isTrusted — they only need
          // the user activation a preceding trusted click already granted.
          E.jsClick = (kind) => {
            const b = E.button(kind);
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
            const b = E.button(kind);
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
                CanDictate: Flag(r, "canDictate"),
                LoginMarker: Flag(r, "loginMarker"),
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

    public async Task<CaptureState> CaptureStateAsync() {
        string json = await EvalAsync("__echotype.captureState()");
        if (json is "undefined" or "null") {
            throw new DriverException(DriverFailure.NotReady, $"capture state unavailable ({json})");
        }
        try {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new CaptureState(
                IntendedListen: r.TryGetProperty("listen", out var listen) && listen.ValueKind == JsonValueKind.True,
                MicLive: r.TryGetProperty("micLive", out var mic) && mic.ValueKind == JsonValueKind.True,
                UiDictating: r.TryGetProperty("ui", out var ui) && ui.ValueKind == JsonValueKind.True,
                Waveform: r.TryGetProperty("wave", out var wave) && wave.ValueKind == JsonValueKind.True,
                Gum: r.TryGetProperty("gum", out var gum) ? gum.GetString() ?? "none" : "none",
                Composer: r.TryGetProperty("composer", out var composer) ? composer.GetString() ?? "" : "",
                LastUser: r.TryGetProperty("user", out var user) ? user.GetString() ?? "" : "");
        } catch (DriverException) {
            throw;
        } catch (Exception ex) {
            throw new DriverException(DriverFailure.JavaScript, "bad capture JSON: " + ex.Message);
        }
    }

    /// <summary>
    /// Watches the model's mic/waveform while EchoType still thinks it is listening.
    /// Returns a short reason once capture ends on its own, or null if cancelled.
    /// </summary>
    public async Task<string?> AwaitUnexpectedStopAsync(CancellationToken cancel) {
        bool armed = false;
        bool sawUi = false;
        bool sawMic = false;
        int deadStreak = 0;
        DateTime started = DateTime.UtcNow;
        string userBefore = _userTextBeforeDictation;

        while (!cancel.IsCancellationRequested) {
            CaptureState cap;
            try {
                cap = await CaptureStateAsync();
            } catch (DriverException ex) {
                Log.Write("driver: capture probe failed (" + ex.Message + ")");
                try {
                    await Task.Delay(200, cancel);
                } catch (OperationCanceledException) {
                    return null;
                }
                continue;
            }

            bool uiActive = cap.UiDictating || cap.Waveform;
            bool micActive = cap.MicLive;
            if (uiActive) {
                sawUi = true;
            }
            if (micActive) {
                sawMic = true;
            }
            if (!armed && (uiActive || micActive || cap.Gum is "ok" or "requested")) {
                armed = true;
                deadStreak = 0;
                Log.Write("driver: recording armed (mic=" + cap.MicLive
                    + " ui=" + cap.UiDictating
                    + " wave=" + cap.Waveform
                    + " gum=" + cap.Gum + ")");
            }

            string? reason = null;
            bool userChanged = cap.LastUser.Length > 0 && cap.LastUser != userBefore;
            if (armed && (sawUi || sawMic)) {
                bool uiGone = sawUi && !uiActive;
                bool micGone = sawMic && !micActive && !uiActive;
                bool gumEnded = cap.Gum == "ended" && !uiActive;
                bool autoSent = userChanged && !uiActive;
                if (uiGone || micGone || gumEnded || autoSent) {
                    deadStreak++;
                    if (deadStreak >= 3) {
                        reason = autoSent ? "auto-sent"
                            : uiGone ? "waveform-ended"
                            : gumEnded ? "mic-ended"
                            : "recording-stopped";
                    }
                } else {
                    deadStreak = 0;
                }
            } else if (armed && cap.Gum == "ended" && !uiActive && !micActive) {
                deadStreak++;
                if (deadStreak >= 3) {
                    reason = "mic-ended";
                }
            } else {
                deadStreak = 0;
            }

            if (reason != null && DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(700)) {
                Log.Write("driver: unexpected recording stop (" + reason
                    + " mic=" + cap.MicLive
                    + " ui=" + cap.UiDictating
                    + " wave=" + cap.Waveform
                    + " gum=" + cap.Gum + ")");
                return reason;
            }

            try {
                await Task.Delay(150, cancel);
            } catch (OperationCanceledException) {
                return null;
            }
        }
        return null;
    }

    private static string Text(JsonElement obj, string name) {
        if (!obj.TryGetProperty(name, out var v)) return "?";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "?" : v.GetRawText();
    }

    private static bool Flag(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ------------------------------------------------------------------
    // CDP input synthesis
    // ------------------------------------------------------------------

    /// <summary>
    /// Ctrl+Shift+D toggles ChatGPT dictation and stops Gemini voice input.
    /// Modifier keys are dispatched separately so page listeners that check
    /// e.ctrlKey/e.shiftKey on a real Control/Shift keydown still fire.
    /// </summary>
    private async Task SendDictationShortcutAsync() {
        const uint ctrl = 2;
        const uint shift = 8;
        await CdpKeyEventAsync("keyDown", vk: 0x11, key: "Control", code: "ControlLeft", modifiers: ctrl, location: 1);
        await CdpKeyEventAsync("keyDown", vk: 0x10, key: "Shift", code: "ShiftLeft", modifiers: ctrl | shift, location: 1);
        await CdpKeyEventAsync("rawKeyDown", vk: 0x44, key: "D", code: "KeyD", modifiers: ctrl | shift, location: 0);
        await CdpKeyEventAsync("keyUp", vk: 0x44, key: "D", code: "KeyD", modifiers: ctrl | shift, location: 0);
        await CdpKeyEventAsync("keyUp", vk: 0x10, key: "Shift", code: "ShiftLeft", modifiers: ctrl, location: 1);
        await CdpKeyEventAsync("keyUp", vk: 0x11, key: "Control", code: "ControlLeft", modifiers: 0, location: 1);
        Log.Write("driver: sent Ctrl+Shift+D");
    }

    private async Task SendEscapeAsync() {
        await CdpKeyAsync(vk: 0x1B, key: "Escape", code: "Escape", modifiers: 0);
        Log.Write("driver: sent Esc");
    }

    private async Task CdpKeyAsync(uint vk, string key, string code, uint modifiers) {
        await CdpKeyEventAsync("rawKeyDown", vk, key, code, modifiers, location: 0);
        await CdpKeyEventAsync("keyUp", vk, key, code, modifiers, location: 0);
    }

    private async Task CdpKeyEventAsync(string type, uint vk, string key, string code, uint modifiers, uint location) {
        string payload = FormattableString.Invariant($$"""
            {"type":"{{type}}","modifiers":{{modifiers}},"windowsVirtualKeyCode":{{vk}},"code":"{{code}}","key":"{{key}}","autoRepeat":false,"location":{{location}}}
            """);
        try {
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", payload));
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

    /// <summary>
    /// WebView2 returns the CDP JSON payload. Methods with no return value succeed as
    /// <c>{}</c> (and sometimes as an empty string). A real failure is either a thrown
    /// exception from CallDevToolsProtocolMethodAsync or a JSON object with "error".
    /// </summary>
    private static void CheckCdp(string result) {
        if (string.IsNullOrWhiteSpace(result) || result == "null") {
            return;
        }
        try {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
                return;
            }
            if (root.ValueKind == JsonValueKind.Object) {
                if (root.TryGetProperty("error", out var err)
                    && err.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined) {
                    throw new DriverException(DriverFailure.JavaScript, "CDP error: " + err.GetRawText());
                }
                return; // "{}" and any result object without error = success
            }
        } catch (JsonException) {
            // fall through and report the raw payload
        }
        throw new DriverException(DriverFailure.JavaScript, "CDP error: " + result);
    }

    // ------------------------------------------------------------------
    // Start / submit / cancel
    // ------------------------------------------------------------------

    public async Task StartDictationAsync() {
        InvalidateWaits();
        var st = await StateAsync();
        if (!st.LoggedIn) {
            throw new DriverException(DriverFailure.LoggedOut, "logged out");
        }
        if (st.Dictating) {
            return;
        }

        await ClearComposerAsync();
        try {
            _userTextBeforeDictation = await LastUserTextAsync();
        } catch (DriverException) {
            _userTextBeforeDictation = "";
        }
        if (_selectors.UseAccessibilityTranscriptFallback) {
            await SnapshotAxChromeAsync();
        } else {
            _axChromeBeforeDictation.Clear();
        }
        try {
            _ = await EvalAsync("(window.__etGUM = 'none', window.__etListen = false, window.__etMicLive = false, window.__etStreams = [], 'ok')");
        } catch (DriverException) {
            // best effort — a stale gum flag would only make engagement succeed early
        }

        if (_selectors.HasDictationShortcut) {
            await SendDictationShortcutAsync();
            if (await AwaitEngagementAsync(TimeSpan.FromSeconds(1.5), forensics: false)) {
                return;
            }
            Log.Write("driver: shortcut didn't engage, retrying via CDP click");
        } else {
            Log.Write("driver: starting via CDP click (no dictation shortcut)");
        }

        await CdpClickButtonAsync("start");
        if (await AwaitEngagementAsync(TimeSpan.FromSeconds(1.5), forensics: false)) {
            return;
        }

        Log.Write("driver: CDP click didn't engage, retrying via JS pointer events");
        _ = await EvalAsync("__echotype.jsClick('start')");
        await AwaitEngagementAsync(TimeSpan.FromSeconds(4), forensics: true); // throws on failure
    }

    /// <summary>Polls until dictating or the window closes. Throws on probe errors (mac parity).</summary>
    private async Task<bool> AwaitEngagementAsync(TimeSpan window, bool forensics) {
        DateTime deadline = DateTime.UtcNow + window;
        while (true) {
            var st = await StateAsync();
            if (IsEngaged(st)) {
                await MarkListeningAsync(true);
                return true;
            }
            if (DateTime.UtcNow > deadline) {
                if (forensics) {
                    await LogForensicsAsync(st);
                    throw new DriverException(DriverFailure.ButtonNotFound, $"mic didn't start: {st.Gum}");
                }
                return false; // let the caller climb the next synthesis rung
            }
            await Task.Delay(200);
        }
    }

    private bool IsEngaged(PageState st) {
        if (st.Dictating) {
            return true;
        }
        if (!_selectors.TreatGumAsEngaged) {
            return false;
        }
        return st.Gum is "ok" or "requested" || st.Gum.StartsWith("ok", StringComparison.Ordinal);
    }

    private async Task MarkListeningAsync(bool on) {
        try {
            _ = await EvalAsync("__echotype.markListening(" + (on ? "true" : "false") + ")");
        } catch (DriverException ex) {
            Log.Write("driver: markListening failed: " + ex.Message);
        }
    }

    public async Task SubmitDictationAsync() {
        // Drop the gum-engagement flag first so isDictating reflects the real UI.
        await MarkListeningAsync(false);
        bool useShortcut = _selectors.HasDictationShortcut || _selectors.HasDictationSubmitShortcut;
        if (useShortcut) {
            await SendDictationShortcutAsync();
            if (await DictationFinishedAsync(TimeSpan.FromSeconds(2.5))) {
                return;
            }
            Log.Write("driver: shortcut didn't submit, falling back to CDP click");
        }

        try {
            await CdpClickButtonAsync("submit");
        } catch (DriverException ex) when (ex.Kind == DriverFailure.ButtonNotFound) {
            Log.Write("driver: submit button gone — retrying Ctrl+Shift+D");
            if (useShortcut) {
                await SendDictationShortcutAsync();
            }
        }
        await MarkListeningAsync(false);
        _ = await DictationFinishedAsync(TimeSpan.FromSeconds(2.5));
    }

    /// <summary>
    /// True once listening has stopped or the transcript is already in the composer.
    /// Probe failures count as finished (mac pollWhileDictating parity).
    /// </summary>
    private async Task<bool> DictationFinishedAsync(TimeSpan window) {
        DateTime deadline = DateTime.UtcNow + window;
        while (true) {
            PageState st;
            try {
                st = await StateAsync();
            } catch (DriverException ex) {
                Log.Write("driver: state probe failed while polling (" + ex.Message + ")");
                return true;
            }
            if (!st.Dictating || st.ComposerText.Length > 0) {
                return true;
            }
            if (DateTime.UtcNow > deadline) {
                return false;
            }
            await Task.Delay(150);
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
        await MarkListeningAsync(false);
        await SendEscapeAsync();
        if (!await PollWhileDictatingAsync(TimeSpan.FromSeconds(1.5))) {
            return;
        }
        if (_selectors.HasDictationSubmitShortcut) {
            Log.Write("driver: Esc didn't cancel, trying Ctrl+Shift+D");
            await SendDictationShortcutAsync();
            if (!await PollWhileDictatingAsync(TimeSpan.FromSeconds(1.5))) {
                return;
            }
        }
        Log.Write("driver: Esc didn't cancel, falling back to CDP click");
        try {
            await CdpClickButtonAsync("cancel");
        } catch (DriverException ex) {
            Log.Write("driver: cancel -> " + ex.Message);
        }
    }

    /// <summary>Stops a streaming model reply, if one is in progress.</summary>
    public async Task StopGenerationAsync() {
        try {
            if (await ReadReplyStateAsync() is not { Generating: true }) {
                return;
            }
            Log.Write("driver: stopping generation");
            if (!await TryClickKindAsync("stop")) {
                _ = await EvalAsync("__echotype.jsClick('stop')");
            }
        } catch (DriverException ex) {
            Log.Write("driver: stop generation skipped (" + ex.Message + ")");
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
    // Custom command: fill composer, send, await reply
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens a fresh thread. Returns true once there are no user/assistant bubbles
    /// (or the page was already on an empty chat). Gemini ignores untrusted JS clicks
    /// and keeps the last thread after a reload, so this uses a trusted CDP click,
    /// opens the sidebar if needed, then falls back to navigating home.
    /// </summary>
    public async Task<bool> StartNewChatAsync() {
        try {
            if (await ReadReplyStateAsync() is { Generating: true }) {
                Log.Write("driver: stopping hung generation before new chat");
                if (!await TryClickKindAsync("stop")) {
                    _ = await EvalAsync("__echotype.jsClick('stop')");
                }
                await Task.Delay(400);
            }
        } catch (DriverException ex) {
            Log.Write("driver: stop-before-new-chat skipped (" + ex.Message + ")");
        }

        bool alreadyFresh = await ConversationIsFreshAsync();
        await TryOpenNewChatAsync();
        if (await AwaitFreshConversationAsync(alreadyFresh ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(5))) {
            Log.Write("driver: new chat ready");
            return true;
        }

        if (!alreadyFresh) {
            Log.Write("driver: first new-chat click left messages, trying sidebar");
            if (await TryClickKindAsync("openSidebar")) {
                await Task.Delay(500);
            }
            await TryClickKindAsync("newChat");
            _ = await EvalAsync("__echotype.newChat()");
            if (await AwaitFreshConversationAsync(TimeSpan.FromSeconds(5))) {
                Log.Write("driver: new chat ready after sidebar");
                return true;
            }
        }

        if (alreadyFresh) {
            Log.Write("driver: already on an empty chat");
            return true;
        }

        if (await NavigateToChatHomeAsync() && await AwaitFreshConversationAsync(TimeSpan.FromSeconds(5))) {
            Log.Write("driver: fresh chat after home navigation");
            return true;
        }

        // Gemini reloads the last thread at /app — click New chat again after the load.
        await TryOpenNewChatAsync();
        if (await AwaitFreshConversationAsync(TimeSpan.FromSeconds(8))) {
            Log.Write("driver: new chat ready after home navigation");
            return true;
        }

        Log.Write("driver: new chat failed — conversation still has messages");
        return false;
    }

    private async Task TryOpenNewChatAsync() {
        if (await TryClickKindAsync("newChat")) {
            return;
        }
        Log.Write("driver: new chat control missing, opening sidebar");
        if (await TryClickKindAsync("openSidebar")) {
            await Task.Delay(500);
            if (await TryClickKindAsync("newChat")) {
                return;
            }
        }
        string result = await EvalAsync("__echotype.newChat()");
        if (result != "ok") {
            Log.Write("driver: new chat skipped (" + result + ")");
        }
    }

    private async Task<bool> TryClickKindAsync(string kind) {
        try {
            await CdpClickButtonAsync(kind);
            return true;
        } catch (DriverException ex) when (ex.Kind == DriverFailure.ButtonNotFound) {
            Log.Write("driver: no CDP target for " + kind);
        } catch (DriverException ex) {
            Log.Write("driver: CDP click " + kind + " failed (" + ex.Message + ")");
        }

        string js = await EvalAsync("__echotype.jsClick('" + kind + "')");
        if (js == "ok") {
            Log.Write("driver: JS click " + kind);
            return true;
        }
        return false;
    }

    private async Task<bool> ConversationIsFreshAsync() {
        try {
            return await EvalAsync("__echotype.conversationFresh()") == "true";
        } catch (DriverException) {
            return false;
        }
    }

    private async Task<bool> AwaitFreshConversationAsync(TimeSpan timeout) {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true) {
            try {
                if (await ConversationIsFreshAsync()) {
                    var st = await StateAsync();
                    if (st.ComposerPresent && !st.Dictating) {
                        return true;
                    }
                }
            } catch (DriverException) {
                // SPA navigation mid-reload
            }
            if (DateTime.UtcNow > deadline) {
                return false;
            }
            await Task.Delay(200);
        }
    }

    private async Task<bool> NavigateToChatHomeAsync() {
        if (string.IsNullOrEmpty(_chatUrl)) {
            return false;
        }
        Log.Write("driver: loading " + _chatUrl + " to force a fresh chat");
        try {
            _webview.Navigate(_chatUrl);
        } catch (Exception ex) {
            Log.Write("driver: home navigation failed: " + ex.Message);
            return false;
        }

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline) {
            try {
                var st = await StateAsync();
                if (st.LoggedIn && st.ComposerPresent && !st.Dictating) {
                    return true;
                }
            } catch (DriverException) {
                // page still swapping
            }
            await Task.Delay(250);
        }
        Log.Write("driver: home navigation did not become interactive in time");
        return false;
    }

    public async Task SetComposerAsync(string text) {
        await ClearComposerAsync();
        string focus = await EvalAsync("__echotype.focusComposer()");
        if (focus != "ok") {
            throw new DriverException(DriverFailure.NotReady, "no composer to fill");
        }
        try {
            await CdpInsertTextAsync(text);
        } catch (Exception ex) {
            Log.Write("driver: CDP insertText failed: " + ex.Message);
        }
        if ((await StateAsync()).ComposerText.Length == 0) {
            Log.Write("driver: CDP insert didn't fill composer, falling back to JS");
            string encoded = JsonSerializer.Serialize(text);
            string result = await EvalAsync("__echotype.setComposer(" + encoded + ")");
            if ((await StateAsync()).ComposerText.Length == 0) {
                throw new DriverException(DriverFailure.JavaScript, "could not fill composer (" + result + ")");
            }
        }
        await PokeComposerAsync();
        await WaitUntilComposerPopulatedAsync(text);
    }

    /// <summary>
    /// Waits until the composer text is stable, then pauses so the page can enable
    /// Send. Large prompts get a 3 s pause — a 400 ms wait is enough for short ones.
    /// </summary>
    private async Task WaitUntilComposerPopulatedAsync(string expected) {
        if (expected.Length < LargeComposerChars) {
            await Task.Delay(400);
            return;
        }

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        string last = "";
        int stable = 0;
        while (DateTime.UtcNow < deadline) {
            string current = (await StateAsync()).ComposerText;
            if (current.Length > 0 && current == last) {
                stable++;
                if (stable >= 3) {
                    break;
                }
            } else {
                stable = 0;
                last = current;
            }
            await Task.Delay(150);
        }

        Log.Write($"driver: composer populated ({last.Length} chars, expected {expected.Length}), waiting 3000ms before send");
        await Task.Delay(3000);
        await PokeComposerAsync();
    }

    private async Task PokeComposerAsync() {
        try {
            _ = await EvalAsync("__echotype.pokeComposer()");
        } catch (DriverException ex) {
            Log.Write("driver: pokeComposer failed: " + ex.Message);
        }
    }

    private async Task CdpInsertTextAsync(string text) {
        const int chunk = 8000;
        for (int i = 0; i < text.Length; i += chunk) {
            int len = Math.Min(chunk, text.Length - i);
            string payload = JsonSerializer.Serialize(new { text = text.Substring(i, len) });
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Input.insertText", payload));
            if (text.Length >= LargeComposerChars && i + len < text.Length) {
                await Task.Delay(120);
            }
        }
        Log.Write($"driver: CDP insertText {text.Length} chars");
    }

    public async Task SendPromptAsync() {
        string userBefore = "";
        try {
            userBefore = await LastUserTextAsync();
        } catch (DriverException) {
            userBefore = "";
        }

        int composerLen = 0;
        try {
            composerLen = (await StateAsync()).ComposerText.Length;
        } catch (DriverException) {
            composerLen = 0;
        }
        TimeSpan readyWindow = composerLen >= LargeComposerChars
            ? TimeSpan.FromSeconds(8)
            : TimeSpan.FromSeconds(5);
        DateTime readyUntil = DateTime.UtcNow + readyWindow;
        while (DateTime.UtcNow < readyUntil) {
            string ready = await EvalAsync("__echotype.sendReady()");
            if (ready == "ok") {
                break;
            }
            await Task.Delay(150);
        }

        try {
            await CdpClickButtonAsync("send");
            if (await AwaitSendAcceptedAsync(TimeSpan.FromSeconds(2.5), userBefore)) {
                return;
            }
        } catch (DriverException ex) when (ex.Kind == DriverFailure.ButtonNotFound) {
            Log.Write("driver: send button not found for CDP click");
        }

        Log.Write("driver: retrying send via JS click");
        _ = await EvalAsync("__echotype.jsClick('send')");
        if (await AwaitSendAcceptedAsync(TimeSpan.FromSeconds(2), userBefore)) {
            return;
        }

        Log.Write("driver: retrying send via Enter");
        _ = await EvalAsync("__echotype.focusComposer()");
        await CdpKeyAsync(vk: 0x0D, key: "Enter", code: "Enter", modifiers: 0);
        if (await AwaitSendAcceptedAsync(TimeSpan.FromSeconds(2.5), userBefore)) {
            return;
        }

        throw new DriverException(DriverFailure.ButtonNotFound, "send didn't land");
    }

    /// <summary>
    /// True once the prompt has left the composer or appeared as a user bubble.
    /// Generating alone is not enough — Gemini's page often has a spinner that
    /// would make us skip the Enter fallback while the prompt is still unsent.
    /// </summary>
    private async Task<bool> AwaitSendAcceptedAsync(TimeSpan window, string userBefore) {
        DateTime deadline = DateTime.UtcNow + window;
        while (true) {
            var st = await ReadReplyStateAsync();
            if (st != null) {
                bool composerLeft = st.Composer.Length == 0;
                bool userChanged = st.User.Length > 0 && st.User != userBefore;
                if (composerLeft || userChanged) {
                    Log.Write(userChanged
                        ? "driver: send accepted (new user message)"
                        : st.Generating
                            ? "driver: send accepted (composer empty, generating)"
                            : "driver: send accepted (composer empty)");
                    return true;
                }
            }
            if (DateTime.UtcNow > deadline) {
                return false;
            }
            await Task.Delay(150);
        }
    }

    public async Task<string> LastAssistantTextAsync() {
        string text = await EvalAsync("__echotype.lastAssistantText()");
        return text is "undefined" or "null" ? "" : text;
    }

    public async Task<string> LastUserTextAsync() {
        string text = await EvalAsync("__echotype.lastUserText()");
        return text is "undefined" or "null" ? "" : text;
    }

    public async Task<ModelReply> LastAssistantReplyAsync() {
        var st = await ReadReplyStateAsync();
        return st?.Reply ?? ModelReply.Empty;
    }

    public async Task<CommandTurnSnapshot> SnapshotCommandTurnAsync() {
        var st = await ReadReplyStateAsync();
        return st == null
            ? CommandTurnSnapshot.Empty
            : new CommandTurnSnapshot(st.Reply, st.UserTurns, st.AssistantTurns, st.AssistantKey);
    }

    /// <summary>
    /// Reads the last assistant bubble after a wait timed out. Used so a reply
    /// that already landed is not reported as missing.
    /// </summary>
    public async Task<ModelReply> TryRecoverAssistantReplyAsync(CommandTurnSnapshot previous) {
        try {
            var st = await ReadReplyStateAsync();
            if (st == null || st.Reply.IsEmpty) {
                return ModelReply.Empty;
            }
            if (ReplyMatchesUserBubble(st.Reply, st.User)) {
                return ModelReply.Empty;
            }
            bool newTurn = st.AssistantTurns > previous.AssistantTurns
                || (st.AssistantKey.Length > 0 && st.AssistantKey != previous.AssistantKey);
            if (!newTurn && Fingerprint(st.Reply) == Fingerprint(previous.Reply)) {
                return ModelReply.Empty;
            }
            Log.Write("driver: recovered assistant reply (" + st.Reply.Text.Length + " chars)");
            return st.Reply;
        } catch (DriverException ex) {
            Log.Write("driver: reply recovery failed: " + ex.Message);
            return ModelReply.Empty;
        }
    }

    public async Task<ModelReply> AwaitAssistantReplyAsync(
        CommandTurnSnapshot previous,
        TimeSpan firstByteTimeout) {
        if (firstByteTimeout < TimeSpan.FromSeconds(5)) {
            firstByteTimeout = TimeSpan.FromSeconds(5);
        }
        int epoch = _waitEpoch;
        TimeSpan inactivity = TimeSpan.FromSeconds(90);
        TimeSpan imageLoadGrace = TimeSpan.FromSeconds(25);
        DateTime firstByteDeadline = DateTime.UtcNow + firstByteTimeout;
        DateTime deadline = DateTime.UtcNow + inactivity;
        DateTime? imageHardDeadline = null;
        DateTime? imageLoadDeadline = null;
        string prevKey = Fingerprint(previous.Reply);
        string lastKey = prevKey;
        ModelReply lastReply = previous.Reply;
        int stableCount = 0;
        bool sawGenerating = false;
        bool sawNewContent = false;
        bool sawImagePending = false;
        bool loggedImageReady = false;
        bool promptLeftComposer = false;
        bool loggedThinking = false;
        bool extendedForThink = false;

        while (true) {
            if (epoch != _waitEpoch) {
                Log.Write("driver: reply wait aborted (superseded)");
                return ModelReply.Empty;
            }
            var st = await ReadReplyStateAsync()
                ?? throw new DriverException(DriverFailure.JavaScript, "reply state unavailable");
            if (epoch != _waitEpoch) {
                Log.Write("driver: reply wait aborted (superseded)");
                return ModelReply.Empty;
            }

            if (st.Composer.Length == 0 || st.UserTurns > previous.UserTurns) {
                promptLeftComposer = true;
                if (!extendedForThink) {
                    extendedForThink = true;
                    deadline = DateTime.UtcNow + inactivity;
                }
            }

            bool newAssistantTurn = st.AssistantTurns > previous.AssistantTurns
                || (st.AssistantKey.Length > 0 && st.AssistantKey != previous.AssistantKey);
            bool replyIsEcho = ReplyMatchesUserBubble(st.Reply, st.User);

            string key = Fingerprint(st.Reply);
            // Only the new turn can be an image job. The previous bubble often
            // still matches the placeholder regex (translations mention images).
            bool placeholderPending = newAssistantTurn
                && !st.Reply.HasLoadedImage
                && !replyIsEcho
                && (st.ImagePending || LooksLikeImagePlaceholder(st.Reply.Text));
            bool imageLoading = newAssistantTurn
                && st.Reply.Images.Count > 0
                && !st.Reply.HasLoadedImage;

            if (sawImagePending && st.Reply.HasLoadedImage && !loggedImageReady) {
                loggedImageReady = true;
                Log.Write("driver: generated image ready (" + st.Reply.Images.Count + ")");
            }

            // Image gen re-enables Send after the placeholder; that is not done.
            if (placeholderPending) {
                if (!sawImagePending) {
                    sawImagePending = true;
                    imageHardDeadline = DateTime.UtcNow + TimeSpan.FromMinutes(4);
                    Log.Write("driver: waiting for generated image (placeholder; send stays active)");
                }
                sawGenerating = true;
                sawNewContent = true;
                stableCount = 0;
                lastKey = key;
                lastReply = st.Reply;
                if (imageHardDeadline is DateTime imageDeadline && DateTime.UtcNow > imageDeadline) {
                    Log.Write("driver: image generation timed out, using whatever landed");
                    return NewTurnReplyOrEmpty(lastReply, prevKey);
                }
                await Task.Delay(400);
                continue;
            }

            if (imageLoading) {
                TimeSpan grace = sawImagePending ? TimeSpan.FromSeconds(90) : imageLoadGrace;
                imageLoadDeadline ??= DateTime.UtcNow + grace;
                sawNewContent = true;
                stableCount = 0;
                lastKey = key;
                lastReply = st.Reply;
                if (DateTime.UtcNow <= imageLoadDeadline.Value) {
                    await Task.Delay(300);
                    continue;
                }
                imageLoadDeadline = null;
            } else {
                imageLoadDeadline = null;
            }

            if (st.Generating) {
                sawGenerating = true;
                stableCount = 0;
                deadline = DateTime.UtcNow + inactivity;
            } else if (!st.Reply.IsEmpty && !replyIsEcho && (key != prevKey || newAssistantTurn)) {
                sawNewContent = true;
                if (key == lastKey) {
                    stableCount++;
                    if (stableCount >= 3) {
                        return st.Reply;
                    }
                } else {
                    stableCount = 0;
                    deadline = DateTime.UtcNow + inactivity;
                }
            }

            lastKey = key;
            if (!replyIsEcho) {
                lastReply = st.Reply;
            }

            // No spinner: if the prompt never left the composer, the send failed
            // and a new chat is worth trying. If it did leave, the model is often
            // thinking with no Stop button — keep waiting instead of giving up
            // while the reply is already on the page.
            if (!sawGenerating && !sawNewContent && DateTime.UtcNow > firstByteDeadline) {
                if (!promptLeftComposer) {
                    Log.Write("driver: no reply started within "
                        + ((int)firstByteTimeout.TotalSeconds) + "s (prompt still in composer)");
                    throw new DriverException(DriverFailure.Timeout, "the model didn't start a reply");
                }
                if (!loggedThinking) {
                    loggedThinking = true;
                    Log.Write("driver: prompt sent, waiting for a reply without a visible spinner");
                }
            }
            if (DateTime.UtcNow > deadline) {
                ModelReply landed = NewTurnReplyOrEmpty(lastReply, prevKey);
                if (sawNewContent && !landed.IsEmpty) {
                    return landed;
                }
                throw new DriverException(DriverFailure.Timeout, "the model reply never settled");
            }
            await Task.Delay(300);
        }
    }

    private static bool ReplyMatchesUserBubble(ModelReply reply, string user) {
        if (reply.IsEmpty || string.IsNullOrWhiteSpace(user)) {
            return false;
        }
        return string.Equals(reply.Text.Trim(), user.Trim(), StringComparison.Ordinal);
    }

    private static ModelReply NewTurnReplyOrEmpty(ModelReply reply, string previousFingerprint) {
        if (reply.IsEmpty || Fingerprint(reply) == previousFingerprint) {
            return ModelReply.Empty;
        }
        return reply;
    }

    /// <summary>
    /// Best-effort download of the largest image in the last assistant turn.
    /// Tries an in-page canvas snapshot, then fetch() inside the page (cookies +
    /// blob: URLs), then an HTTP GET from C#.
    /// </summary>
    public async Task<byte[]?> DownloadBestAssistantImageAsync(ModelReply reply) {
        string encoded = await EvalAsync("__echotype.encodeBestAssistantImage()");
        if (TryParseImagePayload(encoded, out byte[]? fromCanvas, out string canvasSrc)
            && fromCanvas is { Length: > 0 }) {
            Log.Write("driver: encoded assistant image (" + fromCanvas.Length + " bytes)");
            return fromCanvas;
        }

        var urls = new List<string>();
        if (!string.IsNullOrEmpty(canvasSrc)) {
            urls.Add(canvasSrc);
        }
        foreach (var img in reply.Images.OrderByDescending(i => (long)i.Width * i.Height)) {
            if (!string.IsNullOrEmpty(img.Src) && !urls.Contains(img.Src, StringComparer.Ordinal)) {
                urls.Add(img.Src);
            }
        }

        foreach (string url in urls) {
            byte[]? bytes = await FetchImageInPageAsync(url);
            if (bytes is { Length: > 0 }) {
                Log.Write("driver: fetched assistant image in page (" + bytes.Length + " bytes)");
                return bytes;
            }
            bytes = await DownloadUrlAsync(url);
            if (bytes is { Length: > 0 }) {
                Log.Write("driver: downloaded assistant image (" + bytes.Length + " bytes)");
                return bytes;
            }
        }
        return null;
    }

    private async Task<byte[]?> FetchImageInPageAsync(string src) {
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
            byte[] data = FromDataUrl(src);
            return data.Length > 0 ? data : null;
        }

        string encoded = JsonSerializer.Serialize(src);
        _ = await EvalAsync("__echotype.beginFetchImage(" + encoded + ")");
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline) {
            string json = await EvalAsync("__echotype.fetchImageStatus()");
            if (json is "undefined" or "null") {
                await Task.Delay(200);
                continue;
            }
            try {
                using var doc = JsonDocument.Parse(json);
                string status = doc.RootElement.TryGetProperty("status", out var st)
                    ? st.GetString() ?? "" : "";
                if (status == "ok") {
                    string data = doc.RootElement.TryGetProperty("data", out var d)
                        ? d.GetString() ?? "" : "";
                    byte[] bytes = FromDataUrl(data);
                    return bytes.Length > 0 ? bytes : null;
                }
                if (status is "err" or "error") {
                    string err = doc.RootElement.TryGetProperty("error", out var e)
                        ? e.GetString() ?? "" : "";
                    Log.Write("driver: in-page image fetch failed: " + err);
                    return null;
                }
            } catch (Exception ex) {
                Log.Write("driver: in-page image status parse failed: " + ex.Message);
                return null;
            }
            await Task.Delay(200);
        }
        Log.Write("driver: in-page image fetch timed out");
        return null;
    }

    private async Task<byte[]?> DownloadUrlAsync(string src) {
        if (src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
            byte[] data = FromDataUrl(src);
            return data.Length > 0 ? data : null;
        }
        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) {
            return null;
        }

        try {
            var cookies = new CookieContainer();
            try {
                var webCookies = await _webview.CookieManager.GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority) + "/");
                foreach (var c in webCookies) {
                    try {
                        cookies.Add(new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain));
                    } catch {
                        // Domain/path mismatch — skip that cookie.
                    }
                }
            } catch (Exception ex) {
                Log.Write("driver: cookie copy for image download failed: " + ex.Message);
            }

            using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            if (!string.IsNullOrEmpty(_chatUrl)) {
                http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", _chatUrl);
            }
            byte[] bytes = await http.GetByteArrayAsync(uri);
            return LooksLikeImage(bytes) ? bytes : null;
        } catch (Exception ex) {
            Log.Write("driver: HTTP image download failed: " + ex.Message);
            return null;
        }
    }

    private static bool TryParseImagePayload(string json, out byte[]? bytes, out string src) {
        bytes = null;
        src = "";
        if (json is "undefined" or "null" or "") {
            return false;
        }
        try {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            src = r.TryGetProperty("src", out var s) ? s.GetString() ?? "" : "";
            string status = r.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (status == "ok" && r.TryGetProperty("data", out var data)) {
                bytes = FromDataUrl(data.GetString() ?? "");
                return bytes.Length > 0;
            }
            return src.Length > 0;
        } catch {
            return false;
        }
    }

    private static byte[] FromDataUrl(string dataUrl) {
        if (string.IsNullOrEmpty(dataUrl)) {
            return [];
        }
        int comma = dataUrl.IndexOf(',');
        string b64 = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
        try {
            byte[] bytes = Convert.FromBase64String(b64);
            return LooksLikeImage(bytes) ? bytes : [];
        } catch {
            return [];
        }
    }

    private static bool LooksLikeImage(byte[] bytes) {
        if (bytes.Length < 24) {
            return false;
        }
        if (bytes[0] == 0x89 && bytes[1] == 0x50) {
            return true; // PNG
        }
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) {
            return true; // JPEG
        }
        if (bytes[0] == 0x47 && bytes[1] == 0x49) {
            return true; // GIF
        }
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) {
            return true; // BMP
        }
        if (bytes.Length > 12 && bytes[0] == 0x52 && bytes[8] == 0x57) {
            return true; // RIFF....WEBP
        }
        return false;
    }

    private static string Fingerprint(ModelReply reply) =>
        reply.Text + "\n" + string.Join("\0", reply.Images.Select(i =>
            i.Src + ":" + i.Width + "x" + i.Height + (i.Loaded ? ":L" : ":U")));

    /// <summary>
    /// ChatGPT/Gemini image jobs emit a short status line first and leave Send
    /// enabled. That line is not the finished reply. Long answers that merely
    /// mention images (translations, app documentation) must not match.
    /// </summary>
    private static bool LooksLikeImagePlaceholder(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }
        string sample = text.Trim();
        if (sample.Length > 160) {
            return false;
        }
        return Regex.IsMatch(
            sample,
            @"^(generating.{0,40}image|creating.{0,40}image|working on.{0,24}image|image.{0,24}(generat|creat)|جار[يٍ]?\s*إنشاء.{0,20}صور|توليد.{0,20}صور|إنشاء صور|generando.{0,24}imagen|g[eé]n[eé]ration.{0,24}image|正在生成.{0,12}图)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private sealed record ReplyState(
        bool Generating,
        bool ImagePending,
        string Composer,
        ModelReply Reply,
        string User,
        int UserTurns,
        int AssistantTurns,
        string AssistantKey);

    private async Task<ReplyState?> ReadReplyStateAsync() {
        string json = await EvalAsync("__echotype.replyState()");
        if (json is "undefined" or "null") {
            return null;
        }
        try {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new ReplyState(
                Generating: r.GetProperty("generating").GetBoolean(),
                ImagePending: r.TryGetProperty("imagePending", out var pending)
                    && pending.ValueKind == JsonValueKind.True,
                Composer: r.GetProperty("composer").GetString() ?? "",
                Reply: new ModelReply(
                    r.GetProperty("last").GetString() ?? "",
                    ParseImages(r)),
                User: r.TryGetProperty("user", out var user) ? user.GetString() ?? "" : "",
                UserTurns: r.TryGetProperty("userTurns", out var ut) && ut.TryGetInt32(out int uti) ? uti : 0,
                AssistantTurns: r.TryGetProperty("assistantTurns", out var at) && at.TryGetInt32(out int ati) ? ati : 0,
                AssistantKey: r.TryGetProperty("assistantKey", out var ak) ? ak.GetString() ?? "" : "");
        } catch (Exception ex) {
            throw new DriverException(DriverFailure.JavaScript, "bad reply JSON: " + ex.Message);
        }
    }

    private static IReadOnlyList<AssistantImage> ParseImages(JsonElement root) {
        if (!root.TryGetProperty("images", out var arr) || arr.ValueKind != JsonValueKind.Array) {
            return [];
        }
        var list = new List<AssistantImage>();
        foreach (var el in arr.EnumerateArray()) {
            string src = el.TryGetProperty("src", out var s) ? s.GetString() ?? "" : "";
            if (src.Length == 0) {
                continue;
            }
            int width = el.TryGetProperty("width", out var w) && w.TryGetInt32(out int wi) ? wi : 0;
            int height = el.TryGetProperty("height", out var h) && h.TryGetInt32(out int hi) ? hi : 0;
            bool loaded = el.TryGetProperty("loaded", out var l) && l.ValueKind == JsonValueKind.True;
            list.Add(new AssistantImage(src, width, height, loaded));
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Transcript
    // ------------------------------------------------------------------

    /// <summary>
    /// Polls until the transcript settles. No fixed cap (dictations run 30+ min);
    /// the deadline is an inactivity window that extends while the page shows progress.
    /// </summary>
    public async Task<string> AwaitTranscriptAsync(TimeSpan recordingDuration) {
        int epoch = _waitEpoch;
        TimeSpan inactivityWindow = TimeSpan.FromSeconds(Math.Max(60, recordingDuration.TotalSeconds * 0.5));
        DateTime deadline = DateTime.UtcNow + inactivityWindow;
        string lastText = "";
        int stableCount = 0;
        int emptyGrace = 0;
        // STT can take several seconds after the composer returns empty (non-English
        // especially). ChatGPT also auto-sends: the composer stays empty while the
        // transcript shows up as a user bubble, then the model starts a reply.
        int emptyLimit = Math.Max(120, (int)(recordingDuration.TotalSeconds * 8) + 40);
        DateTime lastLog = DateTime.MinValue;
        DateTime lastAx = DateTime.MinValue;
        bool dumpedComposer = false;
        string axHold = "";

        while (true) {
            if (epoch != _waitEpoch) {
                Log.Write("driver: transcript wait aborted (superseded)");
                return "";
            }
            var st = await StateAsync(); // failure → propagate (mac parity)
            string text = st.ComposerText;
            if (DateTime.UtcNow > deadline) {
                if (epoch != _waitEpoch) {
                    Log.Write("driver: transcript wait aborted (superseded)");
                    return "";
                }
                string lateUser = await TryUserTranscriptFallbackAsync(epoch);
                if (lateUser.Length > 0) {
                    return lateUser;
                }
                string lateAx = await TryFreshAccessibilityTranscriptAsync();
                if (lateAx.Length > 0) {
                    Log.Write("driver: transcript via accessibility tree after deadline (" + lateAx.Length + " chars)");
                    return lateAx;
                }
                throw new DriverException(DriverFailure.Timeout, "transcript never settled");
            }
            if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(2)) {
                lastLog = DateTime.UtcNow;
                string userLen = "";
                try {
                    userLen = " user=" + (await LastUserTextAsync()).Length;
                } catch (DriverException) {
                    userLen = "";
                }
                Log.Write($"driver: transcript wait dictating={st.Dictating} composer={st.ComposerPresent} len={text.Length} gum={st.Gum}{userLen}");
            }
            if (_selectors.UseAccessibilityTranscriptFallback
                && text.Length == 0
                && DateTime.UtcNow - lastAx > TimeSpan.FromSeconds(1)) {
                lastAx = DateTime.UtcNow;
                string ax = await TryFreshAccessibilityTranscriptAsync();
                if (ax.Length > 0) {
                    if (axHold.Length == 0) {
                        Log.Write("driver: composer empty in DOM — using accessibility tree (" + ax.Length + " chars)");
                    }
                    axHold = ax;
                }
            }
            if (text.Length == 0 && axHold.Length > 0) {
                text = axHold;
            }
            if (st.Dictating || !st.ComposerPresent) {
                deadline = DateTime.UtcNow + inactivityWindow;
                emptyGrace = 0;
            } else if (text.Length > 0 && text == lastText) {
                stableCount++;
                if (stableCount >= 2) {
                    return text;
                }
            } else if (text.Length == 0) {
                if (!dumpedComposer) {
                    dumpedComposer = true;
                    try {
                        Log.Write("driver: composer debug " + Truncate(await EvalAsync("__echotype.composerDebug()"), 900));
                    } catch (DriverException ex) {
                        Log.Write("driver: composer debug failed: " + ex.Message);
                    }
                }
                string user = await TryUserTranscriptFallbackAsync(epoch);
                if (user.Length > 0) {
                    return user;
                }
                bool generating = false;
                try {
                    generating = await ReadReplyStateAsync() is { Generating: true };
                } catch (DriverException) {
                    generating = false;
                }
                if (generating) {
                    deadline = DateTime.UtcNow + inactivityWindow;
                    emptyGrace = 0;
                    Log.Write("driver: composer empty but model is generating — waiting for user bubble");
                } else {
                    emptyGrace++;
                    if (emptyGrace >= emptyLimit) {
                        string lateAx = await TryFreshAccessibilityTranscriptAsync();
                        if (lateAx.Length > 0) {
                            Log.Write("driver: transcript via accessibility tree after empty wait (" + lateAx.Length + " chars)");
                            return lateAx;
                        }
                        return "";
                    }
                }
            } else {
                stableCount = 0;
                deadline = DateTime.UtcNow + inactivityWindow; // text still arriving
            }
            lastText = text;
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Inventory of textbox values/names already on the page (conversation titles,
    /// search fields) so later AX reads don't treat them as a transcript.
    /// </summary>
    private async Task SnapshotAxChromeAsync() {
        _axChromeBeforeDictation.Clear();
        try {
            foreach (var box in await ReadAxBoxesAsync()) {
                if (box.Value.Length > 0 && !IsUiPlaceholder(box.Value)) {
                    _axChromeBeforeDictation.Add(box.Value);
                }
                if (box.Name.Length > 0 && !IsUiPlaceholder(box.Name)) {
                    _axChromeBeforeDictation.Add(box.Name);
                }
            }
        } catch (Exception ex) {
            Log.Write("driver: AX chrome snapshot failed: " + ex.Message);
        }
        if (_axChromeBeforeDictation.Count > 0) {
            Log.Write("driver: AX chrome snapshot " + _axChromeBeforeDictation.Count + " strings");
        }
    }

    private async Task<string> TryFreshAccessibilityTranscriptAsync() {
        if (!_selectors.UseAccessibilityTranscriptFallback) {
            return "";
        }
        string ax = await TryAccessibilityTranscriptAsync();
        if (ax.Length == 0) {
            return "";
        }
        if (_axChromeBeforeDictation.Contains(ax)) {
            Log.Write("driver: ignoring accessibility text that was already on the page (" + ax.Length + " chars)");
            return "";
        }
        return ax;
    }

    /// <summary>
    /// ChatGPT sometimes paints the transcript in a closed shadow / overlay that
    /// innerText cannot read. The accessibility tree still exposes the textbox value.
    /// Never use a node's accessible name — Gemini conversation titles show up there.
    /// </summary>
    private async Task<string> TryAccessibilityTranscriptAsync() {
        try {
            string bestPrompt = "";
            string bestOther = "";
            foreach (var box in await ReadAxBoxesAsync()) {
                string value = box.Value;
                if (value.Length == 0
                    || IsUiPlaceholder(value)
                    || (box.Placeholder.Length > 0 && value == box.Placeholder)
                    || _axChromeBeforeDictation.Contains(value)) {
                    continue;
                }
                if (LooksLikePromptAxNode(box)) {
                    if (value.Length > bestPrompt.Length) {
                        bestPrompt = value;
                    }
                } else if (value.Length > bestOther.Length) {
                    bestOther = value;
                }
            }
            if (bestPrompt.Length > 0) {
                return bestPrompt;
            }
            return _selectors.UseAccessibilityTranscriptFallback ? bestOther : "";
        } catch (Exception ex) {
            _axUnavailable = true;
            Log.Write("driver: AX transcript fallback failed: " + ex.Message);
            return "";
        }
    }

    private sealed record AxBox(string Role, string Name, string Value, string Placeholder);

    private async Task<IReadOnlyList<AxBox>> ReadAxBoxesAsync() {
        if (_axUnavailable) {
            return [];
        }
        if (!_axEnabled) {
            CheckCdp(await _webview.CallDevToolsProtocolMethodAsync("Accessibility.enable", "{}"));
            _axEnabled = true;
        }
        string raw = await _webview.CallDevToolsProtocolMethodAsync("Accessibility.getFullAXTree", "{}");
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("error", out _)) {
            _axUnavailable = true;
            Log.Write("driver: AX transcript fallback unavailable: " + Truncate(raw, 200));
            return [];
        }
        if (!doc.RootElement.TryGetProperty("nodes", out var nodes)
            || nodes.ValueKind != JsonValueKind.Array) {
            return [];
        }
        var boxes = new List<AxBox>();
        foreach (var node in nodes.EnumerateArray()) {
            if (node.TryGetProperty("ignored", out var ignored)
                && ignored.ValueKind == JsonValueKind.True) {
                continue;
            }
            if (AxProperty(node, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            string role = AxString(node, "role");
            if (role.IndexOf("textbox", StringComparison.OrdinalIgnoreCase) < 0
                && role.IndexOf("comboBox", StringComparison.OrdinalIgnoreCase) < 0
                && !role.Equals("searchBox", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            string value = AxString(node, "value");
            if (value.Length == 0) {
                value = AxProperty(node, "value");
            }
            boxes.Add(new AxBox(
                Role: role,
                Name: AxString(node, "name").Trim(),
                Value: value.Trim(),
                Placeholder: AxProperty(node, "placeholder").Trim()));
        }
        return boxes;
    }

    private static bool LooksLikePromptAxNode(AxBox box) {
        string t = (box.Name + " " + box.Placeholder).Trim();
        if (t.Length == 0) {
            return false;
        }
        return t.Contains("prompt", StringComparison.OrdinalIgnoreCase)
            || t.Contains("chatgpt", StringComparison.OrdinalIgnoreCase)
            || t.Contains("gemini", StringComparison.OrdinalIgnoreCase)
            || t.Contains("ask anything", StringComparison.OrdinalIgnoreCase)
            || t.Contains("composer", StringComparison.OrdinalIgnoreCase)
            || t.Contains("textarea", StringComparison.OrdinalIgnoreCase)
            || IsUiPlaceholder(box.Name);
    }

    private static string AxString(JsonElement node, string name) {
        if (!node.TryGetProperty(name, out var value)) {
            return "";
        }
        return ReadAxValue(value);
    }

    private static string AxProperty(JsonElement node, string name) {
        if (!node.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array) {
            return "";
        }
        foreach (var prop in props.EnumerateArray()) {
            if (prop.TryGetProperty("name", out var n) && n.GetString() == name) {
                return prop.TryGetProperty("value", out var v) ? ReadAxValue(v) : "";
            }
        }
        return "";
    }

    private static string ReadAxValue(JsonElement value) {
        if (value.ValueKind == JsonValueKind.String) {
            return value.GetString() ?? "";
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var inner)) {
            return inner.ValueKind == JsonValueKind.String ? inner.GetString() ?? "" : "";
        }
        return "";
    }

    private static bool IsUiPlaceholder(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return true;
        }
        string t = text.Trim();
        return t.Equals("Ask anything", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Ask ChatGPT", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Ask Gemini", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Message ChatGPT", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Enter a prompt for Gemini", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Enter a prompt", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Type a message", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Search", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Search chats", StringComparison.OrdinalIgnoreCase)
            || t.Equals("Think", StringComparison.OrdinalIgnoreCase)
            || t.Equals("New chat", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("What's on the agenda", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("How can I help", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> TryUserTranscriptFallbackAsync(int epoch = 0) {
        if (!_selectors.UseUserMessageAsTranscriptFallback) {
            return "";
        }
        if (epoch != 0 && epoch != _waitEpoch) {
            return "";
        }
        string user = "";
        try {
            user = await LastUserTextAsync();
        } catch (DriverException) {
            user = "";
        }
        if (user.Length > 0 && user != _userTextBeforeDictation) {
            if (epoch != 0 && epoch != _waitEpoch) {
                return "";
            }
            Log.Write("driver: composer empty after dictation — using last user message (auto-send)");
            await StopUnexpectedGenerationAsync(epoch);
            return user;
        }
        return "";
    }

    // ------------------------------------------------------------------
    // Forensics
    // ------------------------------------------------------------------

    private async Task StopUnexpectedGenerationAsync(int epoch = 0) {
        if (epoch != 0 && epoch != _waitEpoch) {
            return;
        }
        try {
            if (await ReadReplyStateAsync() is { Generating: true }) {
                if (epoch != 0 && epoch != _waitEpoch) {
                    return;
                }
                Log.Write("driver: stopping auto-sent generation so the transcript isn't lost in a reply");
                _ = await EvalAsync("__echotype.jsClick('stop')");
            }
        } catch (DriverException ex) {
            Log.Write("driver: stop-after-autosend skipped (" + ex.Message + ")");
        }
    }

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
