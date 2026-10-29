using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using EchoType.Native;

namespace EchoType.Input;

/// <summary>
/// Text shortcuts in any app: remembers the word being typed (via a second low-level
/// keyboard hook, translated with the foreground window's keyboard layout) and, when
/// Space follows a trigger, swallows that Space, erases the trigger with Backspace and
/// types the expansion plus a space. Expansions with a line break or longer than
/// <see cref="MaxTypedLength"/> are pasted through the clipboard instead, which is
/// restored afterwards.
///
/// The word is forgotten on anything that may move the caret or change the target:
/// Enter, Tab, arrows and other non-character keys, Ctrl/Alt/Win shortcuts, mouse
/// clicks and foreground window changes. Our own windows never expand, so triggers
/// can be typed into the editor. Injected input (including our own typing) is ignored.
///
/// Both hooks live on a dedicated thread that does nothing but pump their messages.
/// Windows waits for every low-level hook on every key and mouse move, so hooks on the
/// busy UI thread (WebView2, paste sleeps) stall input system-wide, and Windows then
/// silently drops them. The Backspace/typing is sent from another thread, outside any
/// hook callback: the injected keys pass through these hooks too, and a callback that
/// waits on them blocks the whole input queue.
/// </summary>
internal sealed class TextExpander : IDisposable {

    private const int BufferSize = 64;
    /// <summary>Longer expansions, and any with a line break, are pasted instead of typed.</summary>
    private const int MaxTypedLength = 200;

    private sealed record Entry(string Trigger, string Text);

    /// <summary>When false keys pass through untouched and nothing is remembered.</summary>
    public volatile bool Enabled;

    private NativeMethods.LowLevelKeyboardProc? _keyProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _keyHook;
    private IntPtr _mouseHook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Thread? _sender;
    private BlockingCollection<Entry>? _pending;
    private volatile Entry[] _entries = [];
    /// <summary>Set from the UI thread; the hook thread clears the word on its next key.</summary>
    private volatile bool _resetRequested;
    // Only touched on the hook thread:
    private readonly char[] _buffer = new char[BufferSize];
    private int _length;
    private readonly byte[] _keyState = new byte[256];
    private readonly char[] _chars = new char[8];
    private IntPtr _lastForeground;
    private bool _swallowSpaceUp;
    private ISynchronizeInvoke? _ui;

    public bool Start(IEnumerable<TextShortcut> shortcuts, bool enabled, ISynchronizeInvoke ui) {
        Stop();
        Update(shortcuts, enabled);
        _ui = ui;
        _pending = new BlockingCollection<Entry>();
        _sender = new Thread(SendLoop) { IsBackground = true, Name = "EchoType text shortcuts (send)" };
        _sender.Start(_pending);
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(HookLoop) {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "EchoType text shortcuts (hook)",
        };
        _hookThread.Start(ready);
        ready.Wait(TimeSpan.FromSeconds(5));
        return _keyHook != IntPtr.Zero;
    }

    public void Update(IEnumerable<TextShortcut> shortcuts, bool enabled) {
        _entries = shortcuts
            .Select(s => new Entry(s.Trigger.Trim().ToLowerInvariant(), s.Text ?? ""))
            .Where(e => TextShortcut.IsValidTrigger(e.Trigger) && e.Text.Length > 0)
            .GroupBy(e => e.Trigger)
            .Select(g => g.First())
            .ToArray();
        Enabled = enabled;
        _resetRequested = true;
    }

    public void Stop() {
        if (_hookThread != null) {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(TimeSpan.FromSeconds(1));
            _hookThread = null;
        }
        if (_pending != null) {
            _pending.CompleteAdding();
            _sender?.Join(TimeSpan.FromSeconds(1));
            _pending = null;
            _sender = null;
        }
    }

    private void HookLoop(object? state) {
        var ready = (ManualResetEventSlim)state!;
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        _keyProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        _keyHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyProc, IntPtr.Zero, 0);
        if (_keyHook == IntPtr.Zero) {
            Log.Write($"text-shortcuts: hook install failed (error {Marshal.GetLastWin32Error()})");
            ready.Set();
            return;
        }
        // Clicks only reset the remembered word; without this hook expansion still works.
        _mouseHook = NativeMethods.SetWindowsHookExMouse(NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        Log.Write("text-shortcuts: hook installed on its own thread (" + _entries.Length
            + " shortcuts, enabled=" + Enabled + ")");
        ready.Set();
        // Low-level hooks are called from inside GetMessage; nothing else is posted here.
        while (NativeMethods.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) {
        }
        NativeMethods.UnhookWindowsHookEx(_keyHook);
        if (_mouseHook != IntPtr.Zero) {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
        }
        _keyHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
        Log.Write("text-shortcuts: hook removed");
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            if (nCode >= 0 && _keyProc != null && HandleKey(wParam, lParam)) {
                return new IntPtr(1);
            }
        } catch (Exception ex) {
            ThreadPool.QueueUserWorkItem(
                static state => Log.Write("text-shortcuts: hook callback error: " + (string)state!),
                ex.Message);
        }
        return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode >= 0) {
            uint msg = (uint)wParam.ToInt64();
            if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN) {
                _length = 0; // the caret may have moved
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>Returns true when the key event should be swallowed.</summary>
    private bool HandleKey(IntPtr wParam, IntPtr lParam) {
        var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if ((kbd.flags & NativeMethods.LLKHF_INJECTED) != 0) {
            return false;
        }
        uint msg = (uint)wParam.ToInt64();
        uint vk = kbd.vkCode;
        if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP) {
            if (vk == NativeMethods.VK_SPACE && _swallowSpaceUp) {
                _swallowSpaceUp = false;
                return true;
            }
            return false;
        }
        if (msg is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)) {
            return false;
        }
        if (_resetRequested) {
            _resetRequested = false;
            _length = 0;
        }
        if (!Enabled || _entries.Length == 0) {
            _length = 0;
            return false;
        }
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _lastForeground) {
            _lastForeground = foreground;
            _length = 0;
        }
        if (IsModifier(vk)) {
            return false;
        }
        bool ctrl = IsDown(NativeMethods.VK_CONTROL);
        bool alt = IsDown(NativeMethods.VK_MENU);
        bool win = IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN);
        // Ctrl+Alt together is AltGr (typing); either one alone, or Win, is a shortcut.
        if (win || ctrl != alt) {
            _length = 0;
            return false;
        }
        if (vk == NativeMethods.VK_SPACE) {
            bool expanded = !ctrl && TryExpand(foreground);
            _length = 0;
            _swallowSpaceUp = expanded;
            return expanded;
        }
        if (vk == NativeMethods.VK_BACK) {
            if (_length > 0) {
                _length--;
            }
            return false;
        }
        Remember(vk, kbd.scanCode, foreground, ctrl);
        return false;
    }

    private void Remember(uint vk, uint scanCode, IntPtr foreground, bool altGr) {
        uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        IntPtr layout = NativeMethods.GetKeyboardLayout(thread);
        Array.Clear(_keyState);
        if (IsDown(NativeMethods.VK_SHIFT)) {
            _keyState[NativeMethods.VK_SHIFT] = 0x80;
        }
        if (altGr) {
            _keyState[NativeMethods.VK_CONTROL] = 0x80;
            _keyState[NativeMethods.VK_MENU] = 0x80;
        }
        if ((NativeMethods.GetKeyState((int)NativeMethods.VK_CAPITAL) & 1) != 0) {
            _keyState[NativeMethods.VK_CAPITAL] = 0x01;
        }
        int count = NativeMethods.ToUnicodeEx(vk, scanCode, _keyState, _chars, _chars.Length,
            NativeMethods.TOUNICODE_NO_STATE_CHANGE, layout);
        if (count < 0) {
            return; // dead key: composes with the next key
        }
        if (count == 0) {
            _length = 0; // arrows, Home, Delete, F-keys… — the caret or text may have changed
            return;
        }
        for (int i = 0; i < count && i < _chars.Length; i++) {
            char c = _chars[i];
            if (char.IsControl(c)) {
                _length = 0; // Enter, Tab, Esc
            } else {
                Append(c);
            }
        }
    }

    private void Append(char c) {
        if (_length == BufferSize) {
            // Only the end of the word can match; keep the newer half.
            Array.Copy(_buffer, BufferSize / 2, _buffer, 0, BufferSize / 2);
            _length = BufferSize / 2;
        }
        _buffer[_length++] = c;
    }

    private bool TryExpand(IntPtr foreground) {
        if (_length == 0) {
            return false;
        }
        Entry? best = null;
        foreach (var entry in _entries) {
            int len = entry.Trigger.Length;
            if (len > _length || (best != null && len <= best.Trigger.Length)) {
                continue;
            }
            if (!BufferEndsWith(entry.Trigger)) {
                continue;
            }
            // Whole words only: "وع" must not fire at the end of a longer word.
            if (_length > len && char.IsLetterOrDigit(_buffer[_length - len - 1])) {
                continue;
            }
            best = entry;
        }
        if (best == null) {
            return false;
        }
        NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == (uint)Environment.ProcessId) {
            return false; // our own editor: let the trigger be typed as-is
        }
        // Never send input from inside the callback; the sender thread types it.
        return _pending?.TryAdd(best) == true;
    }

    private bool BufferEndsWith(string trigger) {
        int start = _length - trigger.Length;
        for (int i = 0; i < trigger.Length; i++) {
            if (char.ToLowerInvariant(_buffer[start + i]) != trigger[i]) {
                return false;
            }
        }
        return true;
    }

    private void SendLoop(object? state) {
        var pending = (BlockingCollection<Entry>)state!;
        foreach (var entry in pending.GetConsumingEnumerable()) {
            try {
                Send(entry);
            } catch (Exception ex) {
                Log.Write("text-shortcuts: expand failed: " + ex.Message);
            }
        }
    }

    private void Send(Entry entry) {
        string text = entry.Text + " ";
        bool paste = text.Length > MaxTypedLength || text.Contains('\n') || text.Contains('\r');
        if (paste) {
            text = text.ReplaceLineEndings("\r\n"); // classic Win32 edit boxes ignore a bare \n
        }
        int erase = entry.Trigger.Count(c => !char.IsLowSurrogate(c));
        var inputs = new List<NativeMethods.INPUT>(erase * 2 + (paste ? 0 : text.Length * 2));
        for (int i = 0; i < erase; i++) {
            inputs.Add(Key(NativeMethods.VK_BACK, 0, 0));
            inputs.Add(Key(NativeMethods.VK_BACK, 0, NativeMethods.KEYEVENTF_KEYUP));
        }
        if (!paste) {
            foreach (char c in text) {
                inputs.Add(Key(0, c, NativeMethods.KEYEVENTF_UNICODE));
                inputs.Add(Key(0, c, NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP));
            }
        }
        uint sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeMethods.INPUT>());
        if (paste) {
            // The clipboard needs the STA UI thread; the Backspaces above are already queued.
            _ui?.BeginInvoke(new Action(() => Paster.PasteRestoringClipboard(text)), null);
        }
        Log.Write("text-shortcuts: expanded a " + erase + "-char trigger, "
            + (paste ? "pasted " : "typed ") + text.Length + " chars"
            + (sent == inputs.Count ? "" : " (SendInput sent " + sent + "/" + inputs.Count + ")"));
    }

    private static NativeMethods.INPUT Key(uint vk, ushort scan, uint flags) {
        var input = new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD };
        input.U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)vk,
            wScan = scan,
            dwFlags = flags,
            dwExtraInfo = NativeMethods.EchoTypeExtraInfo,
        };
        return input;
    }

    private static bool IsDown(uint vk) => (NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static bool IsModifier(uint vk) => vk is NativeMethods.VK_SHIFT or NativeMethods.VK_LSHIFT
        or NativeMethods.VK_RSHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_LCONTROL
        or NativeMethods.VK_RCONTROL or NativeMethods.VK_MENU or NativeMethods.VK_LMENU
        or NativeMethods.VK_RMENU or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN
        or NativeMethods.VK_CAPITAL;

    public void Dispose() => Stop();
}
