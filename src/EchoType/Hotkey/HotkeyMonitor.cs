using System.ComponentModel;
using System.Runtime.InteropServices;
using EchoType.Native;

namespace EchoType.Hotkey;

/// <summary>
/// Global hold-to-talk hotkey via a low-level keyboard hook (HotkeyMonitor.swift /
/// CGEventTap analog). The hook swallows the hotkey so it never types anything, and
/// swallows physical Esc while EscSwallowActive — the no-HUD cancel path.
///
/// The callback must stay allocation-free and IO-free: Windows silently removes a
/// low-level hook whose callback blocks (LowLevelHooksTimeout). All real work is
/// marshaled to the UI thread.
/// </summary>
internal sealed class HotkeyMonitor : IDisposable {

    /// <summary>Raised on the UI thread when the hotkey goes down (non-autorepeat).</summary>
    public event Action? HoldStart;

    /// <summary>Raised on the UI thread when the hotkey is released.</summary>
    public event Action? HoldEnd;

    /// <summary>Raised on the UI thread when Esc is swallowed (only while EscSwallowActive).</summary>
    public event Action? CancelRequested;

    /// <summary>When true the hook swallows Esc presses. Set only while Listening.</summary>
    public volatile bool EscSwallowActive;

    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private uint _vk;
    private bool _held;
    private ISynchronizeInvoke? _ui;
    private SendOrPostCallback? _raiseHoldStart;
    private SendOrPostCallback? _raiseHoldEnd;
    private SendOrPostCallback? _raiseCancel;

    public bool Start(uint hotkeyVk, ISynchronizeInvoke ui) {
        Stop();
        _vk = hotkeyVk;
        _ui = ui;
        _raiseHoldStart = static state => ((HotkeyMonitor)state!).RaiseHoldStart();
        _raiseHoldEnd = static state => ((HotkeyMonitor)state!).RaiseHoldEnd();
        _raiseCancel = static state => ((HotkeyMonitor)state!).RaiseCancel();
        _proc = HookCallback;
        _hook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) {
            Log.Write($"hotkey: hook install failed (error {Marshal.GetLastWin32Error()})");
            _proc = null;
            return false;
        }
        Log.Write($"hotkey: low-level hook installed for VK 0x{hotkeyVk:X2}");
        return true;
    }

    public void Stop() {
        if (_hook != IntPtr.Zero) {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Log.Write("hotkey: hook removed");
        }
        _proc = null;
        _held = false;
    }

    private void RaiseHoldStart() => HoldStart?.Invoke();
    private void RaiseHoldEnd() => HoldEnd?.Invoke();
    private void RaiseCancel() => CancelRequested?.Invoke();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam) {
        if (nCode >= 0 && _proc != null) {
            var kbd = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if ((kbd.flags & NativeMethods.LLKHF_INJECTED) == 0) {
                uint msg = (uint)wParam.ToInt64();
                bool down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                bool up = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;

                if (kbd.vkCode == _vk) {
                    if (down && !_held) {
                        _held = true;                    // LL hook has no autorepeat flag; _held filters it
                        _ui?.BeginInvoke(_raiseHoldStart!, new object?[] { this });
                        return new IntPtr(1);            // swallow — the hotkey never types anything
                    }
                    if (up && _held) {
                        _held = false;
                        _ui?.BeginInvoke(_raiseHoldEnd!, new object?[] { this });
                        return new IntPtr(1);
                    }
                } else if (down && _held && EscSwallowActive && kbd.vkCode == NativeMethods.VK_ESCAPE) {
                    _ui?.BeginInvoke(_raiseCancel!, new object?[] { this });
                    return new IntPtr(1);
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
