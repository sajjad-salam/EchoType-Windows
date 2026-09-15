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
/// Multiple hold keys are supported (dictation + custom commands). Only one can be
/// held at a time. While CaptureKeys is true, every physical key is swallowed and
/// raised as KeyCaptured so the custom-command editor can bind a shortcut.
///
/// The callback must stay allocation-free and IO-free: Windows silently removes a
/// low-level hook whose callback blocks (LowLevelHooksTimeout). All real work is
/// marshaled to the UI thread. The marshal target MUST already have an HWND
/// (call Control.CreateHandle / read Control.Handle) before Start, or BeginInvoke
/// throws and Windows drops the hook — which looks like "Right Ctrl does nothing".
/// </summary>
internal sealed class HotkeyMonitor : IDisposable {

    /// <summary>Raised on the UI thread when a registered hotkey goes down (non-autorepeat).</summary>
    public event Action<uint>? HoldStart;

    /// <summary>Raised on the UI thread when the held hotkey is released.</summary>
    public event Action<uint>? HoldEnd;

    /// <summary>Raised on the UI thread when a tap-style hotkey is pressed (non-autorepeat).</summary>
    public event Action<uint>? TapPressed;

    /// <summary>Raised on the UI thread when Esc is swallowed (only while EscSwallowActive).</summary>
    public event Action? CancelRequested;

    /// <summary>Raised on the UI thread while CaptureKeys is set, once per physical key down.</summary>
    public event Action<uint>? KeyCaptured;

    /// <summary>When true the hook swallows Esc presses. Set while Listening or choosing a command button.</summary>
    public volatile bool EscSwallowActive;

    /// <summary>When true the hook swallows every physical key and reports it via KeyCaptured.</summary>
    public volatile bool CaptureKeys;

    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private volatile uint[] _vkList = [];
    private volatile uint[] _tapList = [];
    private uint _eventVk;
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
    private object?[]? _selfArgs;

    public bool Start(IReadOnlyCollection<uint> holdVks, IReadOnlyCollection<uint> tapVks, ISynchronizeInvoke ui) {
        Stop();
        UpdateHotkeys(holdVks, tapVks);
        _ui = ui;
        _selfArgs = [this];
        _raiseHoldStart = static state => ((HotkeyMonitor)state!).RaiseHoldStart();
        _raiseHoldEnd = static state => ((HotkeyMonitor)state!).RaiseHoldEnd();
        _raiseTap = static state => ((HotkeyMonitor)state!).RaiseTap();
        _raiseCancel = static state => ((HotkeyMonitor)state!).RaiseCancel();
        _raiseCaptured = static state => ((HotkeyMonitor)state!).RaiseCaptured();
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
        Log.Write("hotkey: low-level hook installed for " + FormatVks(_vkList)
            + (_tapList.Length == 0 ? "" : "; tap " + FormatVks(_tapList)));
        return true;
    }

    public void UpdateHotkeys(IReadOnlyCollection<uint> holdVks, IReadOnlyCollection<uint> tapVks) {
        _vkList = holdVks.Where(vk => vk != 0).Distinct().ToArray();
        _tapList = tapVks.Where(vk => vk != 0).Distinct().ToArray();
        if (_hook != IntPtr.Zero) {
            Log.Write("hotkey: watching " + FormatVks(_vkList)
                + (_tapList.Length == 0 ? "" : "; tap " + FormatVks(_tapList)));
        }
    }

    public void Stop() {
        CaptureKeys = false;
        if (_hook != IntPtr.Zero) {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Write("hotkey: hook removed");
        }
        _proc = null;
        _tapDownVk = 0;
        if (_held) {
            _held = false;
            NativeMethods.ReleaseStuckModifiers(_eventVk);
        }
    }

    private void RaiseHoldStart() => HoldStart?.Invoke(_eventVk);

    private void RaiseHoldEnd() {
        NativeMethods.ReleaseStuckModifiers(_eventVk);
        HoldEnd?.Invoke(_eventVk);
    }

    private void RaiseTap() => TapPressed?.Invoke(_tapVk);

    private void RaiseCancel() => CancelRequested?.Invoke();

    private void RaiseCaptured() => KeyCaptured?.Invoke(_capturedVk);

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
                            }
                            // Let Esc/Tab through so the command editor can cancel / change focus.
                        } else if (down) {
                            if (!_captureDown) {
                                _captureDown = true;
                                _capturedVk = distinguished;
                                PostToUi(_raiseCaptured);
                            }
                            return new IntPtr(1);
                        } else if (up) {
                            _captureDown = false;
                            return new IntPtr(1);
                        }
                    } else if (TryMatchList(_tapList, kbd, out uint tap)) {
                        // Tap keys must not participate in the hold state machine:
                        // pressing one during dictation must not end the hold.
                        if (down) {
                            if (_tapDownVk == 0) {
                                _tapDownVk = tap;
                                _tapVk = tap;
                                PostToUi(_raiseTap);
                            }
                            return new IntPtr(1);
                        }
                        if (up) {
                            if (_tapDownVk == tap || _tapDownVk != 0) {
                                _tapDownVk = 0;
                            }
                            return new IntPtr(1);
                        }
                    } else if (TryMatchHotkey(kbd, out uint matched)) {
                        // Swallow every physical event. Auto-repeat KEYDOWNs must not
                        // reach other apps: if they do and we then eat KEYUP, Control
                        // stays stuck and every later keystroke becomes Ctrl+key.
                        if (down) {
                            if (!_held) {
                                _held = true;
                                _eventVk = matched;
                                PostToUi(_raiseHoldStart);
                            }
                            return new IntPtr(1);
                        }
                        if (up) {
                            if (_held) {
                                _held = false;
                                _eventVk = matched;
                                PostToUi(_raiseHoldEnd);
                            }
                            return new IntPtr(1);
                        }
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

    /// <summary>
    /// LL hooks sometimes report Right Ctrl as VK_RCONTROL (0xA3) and sometimes as
    /// VK_CONTROL (0x11) with the extended bit. Same for Left Ctrl without extended.
    /// </summary>
    private bool TryMatchHotkey(NativeMethods.KBDLLHOOKSTRUCT kbd, out uint matched) =>
        TryMatchList(_vkList, kbd, out matched);

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

    public void Dispose() => Stop();
}
