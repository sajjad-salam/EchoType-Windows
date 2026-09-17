using System.ComponentModel;
using System.Runtime.InteropServices;
using EchoType.Native;

namespace EchoType.Hotkey;

/// <summary>
/// Global recording hotkey via a low-level keyboard hook (HotkeyMonitor.swift /
/// CGEventTap analog). The hook swallows every physical hotkey event (first down,
/// auto-repeat downs, and up) so the key cannot type or stick as a modifier in
/// other apps, and swallows physical Esc while EscSwallowActive — the no-HUD cancel
/// path (including after the key is released in press-to-start/stop mode).
///
/// Multiple hold shortcuts are supported (dictation + custom commands). Only one can
/// be held at a time. Custom commands may use one to three keys. Single keys are
/// reserved globally; two- and three-key chords are swallowed only when the full
/// combination is pressed, so Ctrl in Ctrl+Space is not reserved by itself.
///
/// While CaptureKeys is true, every physical key is swallowed and raised as
/// KeyCaptured (one key) or ChordCaptured (up to CaptureMaxKeys) so editors can
/// bind a shortcut.
///
/// The callback must stay allocation-free and IO-free: Windows silently removes a
/// low-level hook whose callback blocks (LowLevelHooksTimeout). All real work is
/// marshaled to the UI thread. The marshal target MUST already have an HWND
/// (call Control.CreateHandle / read Control.Handle) before Start, or BeginInvoke
/// throws and Windows drops the hook — which looks like "Right Ctrl does nothing".
/// </summary>
internal sealed class HotkeyMonitor : IDisposable {

    private const int MaxPressed = 8;

    /// <summary>Raised on the UI thread when a registered hotkey goes down (non-autorepeat).</summary>
    public event Action<HotkeyChord>? HoldStart;

    /// <summary>Raised on the UI thread when the held hotkey is released.</summary>
    public event Action<HotkeyChord>? HoldEnd;

    /// <summary>Raised on the UI thread when a tap-style hotkey is pressed (non-autorepeat).</summary>
    public event Action<uint>? TapPressed;

    /// <summary>Raised on the UI thread when Esc is swallowed (only while EscSwallowActive).</summary>
    public event Action? CancelRequested;

    /// <summary>Raised on the UI thread while CaptureKeys is set, once per physical key down.</summary>
    public event Action<uint>? KeyCaptured;

    /// <summary>Raised on the UI thread while capturing a 1–3 key custom-command shortcut.</summary>
    public event Action<HotkeyChord>? ChordCaptured;

    /// <summary>When true the hook swallows Esc presses. Set while Listening or choosing a command button.</summary>
    public volatile bool EscSwallowActive;

    /// <summary>When true the hook swallows every physical key and reports it via KeyCaptured / ChordCaptured.</summary>
    public volatile bool CaptureKeys;

    /// <summary>How many keys CaptureKeys should collect. 1 keeps single-key pickers unchanged; custom commands use 3.</summary>
    public volatile int CaptureMaxKeys = 1;

    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private volatile HotkeyChord[] _holdList = [];
    private volatile uint[] _tapList = [];
    private readonly uint[] _pressed = new uint[MaxPressed];
    private int _pressedCount;
    private readonly uint[] _swallowed = new uint[MaxPressed];
    private int _swallowedCount;
    private readonly uint[] _captureKeys = new uint[HotkeyChord.MaxKeys];
    private int _captureCount;
    private int _capturePhysDown;
    private HotkeyChord _eventChord;
    private uint _eventVk;
    private HotkeyChord _capturedChord;
    private uint _capturedVk;
    private uint _tapVk;
    private uint _tapDownVk;
    private bool _held;
    private bool _captureDown;
    private ISynchronizeInvoke? _ui;
    private SendOrPostCallback? _raiseHoldStart;
    private SendOrPostCallback? _raiseHoldEnd;
    private SendOrPostCallback? _raiseTap;
    private SendOrPostCallback? _raiseCancel;
    private SendOrPostCallback? _raiseCaptured;
    private SendOrPostCallback? _raiseChordCaptured;
    private object?[]? _selfArgs;

    public bool Start(IReadOnlyCollection<HotkeyChord> holdKeys, IReadOnlyCollection<uint> tapVks, ISynchronizeInvoke ui) {
        Stop();
        UpdateHotkeys(holdKeys, tapVks);
        _ui = ui;
        _selfArgs = [this];
        _raiseHoldStart = static state => ((HotkeyMonitor)state!).RaiseHoldStart();
        _raiseHoldEnd = static state => ((HotkeyMonitor)state!).RaiseHoldEnd();
        _raiseTap = static state => ((HotkeyMonitor)state!).RaiseTap();
        _raiseCancel = static state => ((HotkeyMonitor)state!).RaiseCancel();
        _raiseCaptured = static state => ((HotkeyMonitor)state!).RaiseCaptured();
        _raiseChordCaptured = static state => ((HotkeyMonitor)state!).RaiseChordCaptured();
        _proc = HookCallback;
        // WH_KEYBOARD_LL runs in this process; passing null for hMod is valid and
        // avoids GetModuleHandle issues with single-file published exes.
        _hook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hook == IntPtr.Zero) {
            Log.Write($"hotkey: hook install failed (error {Marshal.GetLastWin32Error()})");
            _proc = null;
            return false;
        }
        Log.Write("hotkey: low-level hook installed for " + FormatChords(_holdList)
            + (_tapList.Length == 0 ? "" : "; tap " + FormatVks(_tapList)));
        return true;
    }

    public void UpdateHotkeys(IReadOnlyCollection<HotkeyChord> holdKeys, IReadOnlyCollection<uint> tapVks) {
        _holdList = holdKeys.Where(c => !c.IsEmpty).Distinct().ToArray();
        _tapList = tapVks.Where(vk => vk != 0).Distinct().ToArray();
        if (_hook != IntPtr.Zero) {
            Log.Write("hotkey: watching " + FormatChords(_holdList)
                + (_tapList.Length == 0 ? "" : "; tap " + FormatVks(_tapList)));
        }
    }

    public void Stop() {
        CaptureKeys = false;
        CaptureMaxKeys = 1;
        if (_hook != IntPtr.Zero) {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Write("hotkey: hook removed");
        }
        _proc = null;
        _tapDownVk = 0;
        _pressedCount = 0;
        _swallowedCount = 0;
        _captureCount = 0;
        _capturePhysDown = 0;
        if (_held) {
            _held = false;
            NativeMethods.ReleaseStuckModifiers(_eventVk);
        }
    }

    private void RaiseHoldStart() => HoldStart?.Invoke(_eventChord);

    private void RaiseHoldEnd() {
        NativeMethods.ReleaseStuckModifiers(_eventVk);
        HoldEnd?.Invoke(_eventChord);
    }

    private void RaiseTap() => TapPressed?.Invoke(_tapVk);

    private void RaiseCancel() => CancelRequested?.Invoke();

    private void RaiseCaptured() => KeyCaptured?.Invoke(_capturedVk);

    private void RaiseChordCaptured() => ChordCaptured?.Invoke(_capturedChord);

    private void PostToUi(SendOrPostCallback? callback) {
        if (callback == null || _ui == null) {
            return;
        }
        try {
            _ui.BeginInvoke(callback, _selfArgs);
        } catch (Exception ex) {
            // Never throw out of the hook. Log on a thread-pool thread so we
            // don't block LowLevelHooksTimeout.
            string msg = "hotkey: UI marshal failed: " + ex.Message;
            ThreadPool.QueueUserWorkItem(static state => Log.Write((string)state!), msg);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            if (nCode >= 0 && _proc != null) {
                var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if ((kbd.flags & NativeMethods.LLKHF_INJECTED) == 0
                    && kbd.dwExtraInfo != NativeMethods.EchoTypeExtraInfo) {
                    uint msg = (uint)wParam.ToInt64();
                    bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                    bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

                    if (CaptureKeys) {
                        uint distinguished = DistinguishedVk(kbd);
                        if (kbd.vkCode == NativeMethods.VK_ESCAPE
                            || distinguished == NativeMethods.VK_ESCAPE
                            || kbd.vkCode == NativeMethods.VK_TAB) {
                            if (up) {
                                _captureDown = false;
                                if (_capturePhysDown > 0) {
                                    _capturePhysDown--;
                                }
                            }
                            // Let Esc/Tab through so the command editor can cancel / change focus.
                        } else if (down) {
                            HandleCaptureDown(distinguished);
                            return new IntPtr(1);
                        } else if (up) {
                            _captureDown = false;
                            if (_capturePhysDown > 0) {
                                _capturePhysDown--;
                            }
                            return new IntPtr(1);
                        }
                    } else if (TryMatchList(_tapList, kbd, out uint tap)) {
                        // Tap keys must not participate in the hold state machine:
                        // pressing one during dictation must not end the hold.
                        if (down) {
                            AddPressed(DistinguishedVk(kbd));
                            if (_tapDownVk == 0) {
                                _tapDownVk = tap;
                                _tapVk = tap;
                                PostToUi(_raiseTap);
                            }
                            return new IntPtr(1);
                        }
                        if (up) {
                            RemovePressed(DistinguishedVk(kbd));
                            if (_tapDownVk == tap || _tapDownVk != 0) {
                                _tapDownVk = 0;
                            }
                            return new IntPtr(1);
                        }
                    } else if (TryHandleHold(kbd, down, up)) {
                        return new IntPtr(1);
                    } else if (down && EscSwallowActive && kbd.vkCode == NativeMethods.VK_ESCAPE) {
                        // Swallow Esc for the whole Listening phase, including toggle
                        // mode after the recording key has been released.
                        PostToUi(_raiseCancel);
                        return new IntPtr(1);
                    }
                }
            }
        } catch (Exception ex) {
            ThreadPool.QueueUserWorkItem(
                static state => Log.Write("hotkey: hook callback error: " + (string)state!),
                ex.Message);
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleCaptureDown(uint distinguished) {
        int max = CaptureMaxKeys;
        if (max < 1) {
            max = 1;
        }
        if (max > HotkeyChord.MaxKeys) {
            max = HotkeyChord.MaxKeys;
        }
        if (max == 1) {
            if (!_captureDown) {
                _captureDown = true;
                _capturedVk = distinguished;
                PostToUi(_raiseCaptured);
            }
            return;
        }
        if (_capturePhysDown == 0) {
            _captureCount = 0;
        }
        if (!CaptureContains(distinguished) && _captureCount < max) {
            _captureKeys[_captureCount++] = distinguished;
        }
        _capturePhysDown++;
        _capturedChord = HotkeyChord.FromCaptured(_captureKeys, _captureCount);
        PostToUi(_raiseChordCaptured);
    }

    private bool CaptureContains(uint vk) {
        for (int i = 0; i < _captureCount; i++) {
            if (HotkeyChord.SameKey(_captureKeys[i], vk) || _captureKeys[i] == vk) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Hold matching plus swallowing. Returns true when the event should be eaten.
    /// </summary>
    private bool TryHandleHold(NativeMethods.KBDLLHOOKSTRUCT kbd, bool down, bool up) {
        uint distinguished = DistinguishedVk(kbd);
        if (down) {
            AddPressed(distinguished);
            if (!TryMatchBestChord(distinguished, kbd.vkCode, out HotkeyChord matched)) {
                return false;
            }
            if (!_held) {
                _held = true;
                _eventChord = matched;
                _eventVk = distinguished;
                PostToUi(_raiseHoldStart);
            }
            AddSwallowed(distinguished);
            return true;
        }
        if (up) {
            bool inHeld = _held && _eventChord.ContainsPhysical(distinguished, kbd.vkCode);
            bool reservedSingle = IsSingleReserved(distinguished, kbd.vkCode);
            bool wasSwallowed = RemoveSwallowed(distinguished);
            if (inHeld) {
                _held = false;
                PostToUi(_raiseHoldEnd);
            }
            RemovePressed(distinguished);
            return wasSwallowed || reservedSingle;
        }
        return false;
    }

    private bool TryMatchBestChord(uint distinguished, uint rawVk, out HotkeyChord matched) {
        HotkeyChord[] list = _holdList;
        HotkeyChord best = default;
        int bestCount = 0;
        for (int i = 0; i < list.Length; i++) {
            HotkeyChord chord = list[i];
            if (chord.Count <= bestCount) {
                continue;
            }
            if (chord.MatchesPressed(_pressed, _pressedCount, distinguished, rawVk)) {
                best = chord;
                bestCount = chord.Count;
            }
        }
        matched = best;
        return bestCount > 0;
    }

    private bool IsSingleReserved(uint distinguished, uint rawVk) {
        HotkeyChord[] list = _holdList;
        for (int i = 0; i < list.Length; i++) {
            HotkeyChord chord = list[i];
            if (chord.Count == 1 && (chord.K1 == distinguished || chord.K1 == rawVk)) {
                return true;
            }
        }
        return false;
    }

    private void AddPressed(uint vk) {
        for (int i = 0; i < _pressedCount; i++) {
            if (_pressed[i] == vk) {
                return;
            }
        }
        if (_pressedCount < MaxPressed) {
            _pressed[_pressedCount++] = vk;
        }
    }

    private void RemovePressed(uint vk) {
        for (int i = 0; i < _pressedCount; i++) {
            if (_pressed[i] == vk) {
                _pressed[i] = _pressed[_pressedCount - 1];
                _pressedCount--;
                return;
            }
        }
    }

    private void AddSwallowed(uint vk) {
        for (int i = 0; i < _swallowedCount; i++) {
            if (_swallowed[i] == vk) {
                return;
            }
        }
        if (_swallowedCount < MaxPressed) {
            _swallowed[_swallowedCount++] = vk;
        }
    }

    private bool RemoveSwallowed(uint vk) {
        for (int i = 0; i < _swallowedCount; i++) {
            if (_swallowed[i] == vk) {
                _swallowed[i] = _swallowed[_swallowedCount - 1];
                _swallowedCount--;
                return true;
            }
        }
        return false;
    }

    private static bool TryMatchList(uint[] list, NativeMethods.KBDLLHOOKSTRUCT kbd, out uint matched) {
        uint distinguished = DistinguishedVk(kbd);
        for (int i = 0; i < list.Length; i++) {
            uint vk = list[i];
            if (vk == distinguished || vk == kbd.vkCode) {
                matched = vk;
                return true;
            }
        }
        matched = 0;
        return false;
    }

    private static uint DistinguishedVk(NativeMethods.KBDLLHOOKSTRUCT kbd) {
        bool extended = (kbd.flags & NativeMethods.LLKHF_EXTENDED) != 0;
        return kbd.vkCode switch {
            NativeMethods.VK_CONTROL => extended ? NativeMethods.VK_RCONTROL : NativeMethods.VK_LCONTROL,
            NativeMethods.VK_MENU => extended ? NativeMethods.VK_RMENU : NativeMethods.VK_LMENU,
            NativeMethods.VK_SHIFT => kbd.scanCode == 0x36 ? NativeMethods.VK_RSHIFT : NativeMethods.VK_LSHIFT,
            _ => kbd.vkCode,
        };
    }

    private static string FormatVks(uint[] vks) =>
        vks.Length == 0 ? "(none)" : string.Join(", ", vks.Select(vk => $"0x{vk:X2}"));

    private static string FormatChords(HotkeyChord[] chords) =>
        chords.Length == 0 ? "(none)" : string.Join(", ", chords.Select(HotkeyNames.For));

    public void Dispose() => Stop();
}
