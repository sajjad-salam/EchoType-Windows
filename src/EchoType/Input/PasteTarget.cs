using System.Diagnostics;
using System.Text;
using EchoType.Native;

namespace EchoType.Input;

/// <summary>
/// The window, focused control, and virtual desktop the user was in when they
/// started holding the dictation key. Restored just before paste so the
/// transcript lands in that field even if they switched apps or desktops while
/// waiting for ChatGPT/Gemini. After paste, the window they were actually using
/// is restored the same way so processing does not leave them in the target app.
/// </summary>
internal sealed class PasteTarget {
    private readonly IntPtr _hwnd;
    private readonly IntPtr _focusHwnd;
    private readonly uint _threadId;
    private readonly string _description;

    private PasteTarget(IntPtr hwnd, IntPtr focusHwnd, uint threadId, string description) {
        _hwnd = hwnd;
        _focusHwnd = focusHwnd;
        _threadId = threadId;
        _description = description;
    }

    public string Description => _description;

    public bool StillExists => NativeMethods.IsWindow(_hwnd);

    /// <summary>
    /// True for classic Win32 edit/rich-edit controls that accept <c>WM_PASTE</c>
    /// without being the foreground window. Chromium, Qt, and Electron fields do not.
    /// </summary>
    public bool AcceptsBackgroundPaste =>
        NativeMethods.IsWindow(_focusHwnd) && IsNativeEdit(_focusHwnd);

    /// <summary>
    /// Snapshot the foreground window and its focused child. Returns null when
    /// there is nowhere sensible to paste (our own windows, desktop, taskbar,
    /// Explorer) so the caller can leave the text on the clipboard instead.
    /// </summary>
    public static PasteTarget? Capture() {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Paster.LooksLikeTextTarget(hwnd)) {
            Log.Write("paste: no capture (hwnd=0x" + hwnd.ToInt64().ToString("X")
                + (hwnd == IntPtr.Zero ? ")" : " class=" + ClassName(hwnd) + ")"));
            return null;
        }

        return FromWindow(hwnd, "captured");
    }

    /// <summary>
    /// Snapshot whatever the user is looking at now, so focus can be handed back
    /// after paste. Returns null when they never left the paste target, or when
    /// the foreground window is one of EchoType's own non-interactive surfaces.
    /// </summary>
    public static PasteTarget? CaptureForegroundExcept(PasteTarget? except) {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) {
            return null;
        }
        if (except != null && except.Owns(hwnd)) {
            return null;
        }
        if (IsOurNonInteractiveWindow(hwnd)) {
            return null;
        }
        return FromWindow(hwnd, "resume");
    }

    /// <summary>
    /// Switch back to this window's virtual desktop, raise it, and put keyboard
    /// focus on the original control. Returns false if the window is gone or
    /// could not be made foreground — the caller should then leave the text on
    /// the clipboard rather than paste into whatever is focused now.
    /// When <paramref name="required"/> is false (handing focus back after paste),
    /// failure is logged and ignored so a successful paste is not rolled back.
    /// </summary>
    public bool Restore(bool required = true) {
        string label = required ? "original window" : "user window";
        if (!NativeMethods.IsWindow(_hwnd)) {
            Log.Write("paste: " + label + " is gone, " + _description);
            return !required;
        }

        try {
            bool switched = VirtualDesktops.SwitchToDesktopOf(_hwnd);
            VirtualDesktops.TrySwitchToView(_hwnd);
            ForceForeground();
            RestoreChildFocus();

            if (IsForegroundOurs()) {
                Log.Write("paste: restored " + (required ? "" : "user focus ")
                    + _description + (switched ? "" : " (desktop switch skipped/failed)"));
                return true;
            }

            Log.Write("paste: could not foreground " + label + ", " + _description);
            return !required;
        } catch (Exception ex) {
            Log.Write("paste: restore threw: " + ex.Message);
            return !required;
        }
    }

    /// <summary>
    /// Inserts the current clipboard into the captured native edit without
    /// activating the window. Returns false when the control is gone or is not
    /// a Win32 edit — the caller should then steal focus and send Ctrl+V.
    /// </summary>
    public bool TryBackgroundPaste() {
        if (!AcceptsBackgroundPaste) {
            return false;
        }
        NativeMethods.SendMessageW(_focusHwnd, NativeMethods.WM_PASTE, IntPtr.Zero, IntPtr.Zero);
        Log.Write("paste: background WM_PASTE to native edit, " + _description);
        return true;
    }

    public bool Owns(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) {
            return false;
        }
        if (hwnd == _hwnd || hwnd == _focusHwnd) {
            return true;
        }
        return NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) == _hwnd;
    }

    private void ForceForeground() {
        NativeMethods.LockSetForegroundWindow(NativeMethods.LSFW_UNLOCK);
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

        if (NativeMethods.IsIconic(_hwnd)) {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE);
        } else {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
        }

        uint ourThread = NativeMethods.GetCurrentThreadId();
        uint targetThread = _threadId != 0
            ? _threadId
            : NativeMethods.GetWindowThreadProcessId(_hwnd, out _);
        uint fgThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);

        uint timeout = 0;
        bool gotTimeout = NativeMethods.SystemParametersInfoW(
            NativeMethods.SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref timeout, 0);
        if (gotTimeout) {
            NativeMethods.SystemParametersInfoW(
                NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE);
        }

        bool attachedFg = false;
        bool attachedTarget = false;
        try {
            attachedFg = fgThread != 0 && fgThread != ourThread
                && NativeMethods.AttachThreadInput(ourThread, fgThread, true);
            attachedTarget = targetThread != 0 && targetThread != ourThread && targetThread != fgThread
                && NativeMethods.AttachThreadInput(ourThread, targetThread, true);

            NativeMethods.BringWindowToTop(_hwnd);
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            bool ok = NativeMethods.SetForegroundWindow(_hwnd);

            if (!ok || !IsForegroundOurs()) {
                NativeMethods.PulseAlt();
                NativeMethods.SetForegroundWindow(_hwnd);
            }
        } finally {
            if (attachedTarget) {
                NativeMethods.AttachThreadInput(ourThread, targetThread, false);
            }
            if (attachedFg) {
                NativeMethods.AttachThreadInput(ourThread, fgThread, false);
            }
            if (gotTimeout) {
                NativeMethods.SystemParametersInfoW(
                    NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, (IntPtr)timeout, NativeMethods.SPIF_SENDCHANGE);
            }
        }
    }

    private void RestoreChildFocus() {
        if (_focusHwnd == IntPtr.Zero || _focusHwnd == _hwnd || !NativeMethods.IsWindow(_focusHwnd)) {
            return;
        }
        uint ourThread = NativeMethods.GetCurrentThreadId();
        uint targetThread = NativeMethods.GetWindowThreadProcessId(_focusHwnd, out _);
        bool attached = false;
        try {
            attached = targetThread != 0 && targetThread != ourThread
                && NativeMethods.AttachThreadInput(ourThread, targetThread, true);
            NativeMethods.SetFocus(_focusHwnd);
        } finally {
            if (attached) {
                NativeMethods.AttachThreadInput(ourThread, targetThread, false);
            }
        }
    }

    private bool IsForegroundOurs() => Owns(NativeMethods.GetForegroundWindow());

    private static PasteTarget FromWindow(IntPtr hwnd, string logPrefix) {
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        var info = new NativeMethods.GUITHREADINFO {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };
        IntPtr focus = hwnd;
        if (threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref info)
            && info.hwndFocus != IntPtr.Zero) {
            focus = info.hwndFocus;
        }

        string className = ClassName(hwnd);
        string title = WindowTitle(hwnd);
        string process = ProcessName(pid);
        VirtualDesktops.TryGetDesktopId(hwnd, out Guid desktop);
        string description = $"{process} hwnd=0x{hwnd.ToInt64():X} class={className} title=\"{Truncate(title, 40)}\" desktop={desktop:N}";
        Log.Write("paste: " + logPrefix + " " + description);
        return new PasteTarget(hwnd, focus, threadId, description);
    }

    /// <summary>
    /// Hidden WebView2 host and the status HUD must never be treated as the
    /// window the user was "reading" — restoring them would yank focus into
    /// an offscreen or overlay surface.
    /// </summary>
    private static bool IsOurNonInteractiveWindow(IntPtr hwnd) {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != (uint)Environment.ProcessId) {
            return false;
        }
        long ex = NativeMethods.GetWindowLongPtrW(hwnd, NativeMethods.GWL_EXSTYLE);
        return (ex & NativeMethods.WS_EX_NOACTIVATE) != 0
            || (ex & NativeMethods.WS_EX_TOOLWINDOW) != 0;
    }

    private static bool IsNativeEdit(IntPtr hwnd) {
        string className = ClassName(hwnd);
        return className is "Edit" or "RichEdit" or "RichEdit20W"
            or "RichEdit20A" or "RICHEDIT50W" or "RichEdit50W"
            or "RICHEDIT60W" or "RichEdit60W";
    }

    private static string ClassName(IntPtr hwnd) {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowTitle(IntPtr hwnd) {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ProcessName(uint pid) {
        try {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        } catch {
            return "pid=" + pid;
        }
    }

    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];
}
