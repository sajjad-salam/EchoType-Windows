using System.Runtime.InteropServices;
using System.Text;
using EchoType.Native;

namespace EchoType.Input;

/// <summary>
/// Reads the text the user had selected in the foreground app at hold-key-down,
/// so dictation can send that selection plus the spoken instruction to the model.
/// Native edit controls are read directly; everything else is copied with a
/// sentinel clipboard round-trip (Ctrl+C). The previous clipboard text is restored.
/// </summary>
internal static class SelectionCapture {

    private const int ClipboardWaitMs = 450;
    private const int ClipboardPollMs = 20;

    /// <summary>
    /// Starts capturing the current selection. Native edits return immediately;
    /// Chromium/Qt apps copy asynchronously, so the task may complete a few
    /// hundred milliseconds later. Must be started on the UI thread. Does not
    /// block dictation start — await the result before composing the model message.
    /// </summary>
    public static Task<string?> CaptureAsync() {
        try {
            IntPtr focus = FocusedControl();
            Log.Write("selection: focus class=" + ClassName(focus));

            if (focus != IntPtr.Zero && TryNativeSelection(focus, out string native)) {
                Log.Write("selection: native capture, " + native.Length + " chars");
                return Task.FromResult<string?>(native);
            }

            return CaptureViaClipboardAsync(focus);
        } catch (Exception ex) {
            Log.Write("selection: capture failed: " + ex.Message);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Builds the composer message: spoken instruction plus the selected text.
    /// The model is asked to return only the replacement so it can be pasted
    /// over the original selection.
    /// </summary>
    public static string BuildModelMessage(string selectedText, string transcript) {
        return "Apply the following instruction to the selected text. "
            + "Reply with only the resulting text — no quotes, preamble, or explanation.\n\n"
            + "Instruction:\n"
            + transcript
            + "\n\nSelected text:\n"
            + selectedText;
    }

    /// <summary>
    /// Ask-model path: send the spoken instruction together with the selection,
    /// without forcing a text-only reply (the model may return an image).
    /// </summary>
    public static string BuildAskModelMessage(string selectedText, string transcript) {
        return transcript.TrimEnd()
            + "\n\nSelected text:\n"
            + selectedText;
    }

    private static async Task<string?> CaptureViaClipboardAsync(IntPtr focusHwnd) {
        string? previous = TryGetClipboardText();
        string sentinel = "\u200B" + Guid.NewGuid().ToString("N");
        if (!SetClipboardText(sentinel)) {
            Log.Write("selection: none (could not set clipboard sentinel)");
            return null;
        }

        uint seq = NativeMethods.GetClipboardSequenceNumber();

        // WM_COPY is enough for some native/Qt widgets; Chromium ignores it.
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (focusHwnd != IntPtr.Zero) {
            NativeMethods.SendMessageW(focusHwnd, NativeMethods.WM_COPY, IntPtr.Zero, IntPtr.Zero);
        }
        if (foreground != IntPtr.Zero && foreground != focusHwnd) {
            NativeMethods.SendMessageW(foreground, NativeMethods.WM_COPY, IntPtr.Zero, IntPtr.Zero);
        }

        string? copied = ReadIfClipboardChanged(sentinel, seq);
        if (copied != null) {
            RestoreClipboardText(previous);
            Log.Write("selection: WM_COPY capture, " + copied.Length + " chars");
            return copied;
        }

        // Physical Right Ctrl is still held (and swallowed). Injected Control
        // KEYDOWN is coalesced unless we first release the stuck modifier, so
        // Chromium never sees a Ctrl+C chord. Clear Control, then send a
        // complete chord with scan codes.
        NativeMethods.ReleaseStuckModifiers(NativeMethods.VK_CONTROL);
        await Task.Delay(25).ConfigureAwait(true);
        seq = NativeMethods.GetClipboardSequenceNumber();
        SynthesizeCtrlC();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ClipboardWaitMs) {
            await Task.Delay(ClipboardPollMs).ConfigureAwait(true);
            copied = ReadIfClipboardChanged(sentinel, seq);
            if (copied != null) {
                RestoreClipboardText(previous);
                Log.Write("selection: clipboard capture, " + copied.Length + " chars");
                return copied;
            }
        }

        RestoreClipboardText(previous);
        Log.Write("selection: none");
        return null;
    }

    private static string? ReadIfClipboardChanged(string sentinel, uint seq) {
        if (NativeMethods.GetClipboardSequenceNumber() == seq) {
            return null;
        }

        string? current = TryGetClipboardText();
        if (current != null && current != sentinel && current.Trim().Length > 0) {
            return current;
        }
        return null;
    }

    private static IntPtr FocusedControl() {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) {
            return IntPtr.Zero;
        }

        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var info = new NativeMethods.GUITHREADINFO {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.GUITHREADINFO>(),
        };
        if (threadId != 0 && NativeMethods.GetGUIThreadInfo(threadId, ref info)
            && info.hwndFocus != IntPtr.Zero) {
            return info.hwndFocus;
        }
        return hwnd;
    }

    private static bool TryNativeSelection(IntPtr hwnd, out string text) {
        text = "";
        if (!IsNativeEdit(hwnd)) {
            return false;
        }

        IntPtr packed = NativeMethods.SendMessageW(
            hwnd, NativeMethods.EM_GETSEL, IntPtr.Zero, IntPtr.Zero);
        int value = unchecked((int)(uint)packed.ToInt64());
        int start = value & 0xFFFF;
        int end = (value >> 16) & 0xFFFF;
        if (end <= start) {
            return false;
        }

        int len = NativeMethods.SendMessageW(
            hwnd, NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero).ToInt32();
        if (len <= 0 || start >= len) {
            return false;
        }

        var sb = new StringBuilder(len + 1);
        NativeMethods.SendMessageW(hwnd, NativeMethods.WM_GETTEXT, (IntPtr)sb.Capacity, sb);
        string full = sb.ToString();
        if (full.Length == 0) {
            return false;
        }

        end = Math.Min(end, full.Length);
        start = Math.Min(start, end);
        if (end <= start) {
            return false;
        }

        text = full[start..end];
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool IsNativeEdit(IntPtr hwnd) {
        string className = ClassName(hwnd);
        return className is "Edit" or "RichEdit" or "RichEdit20W"
            or "RichEdit20A" or "RICHEDIT50W" or "RichEdit50W";
    }

    private static string ClassName(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) {
            return "";
        }
        var sb = new StringBuilder(64);
        NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static void SynthesizeCtrlC() {
        var extra = NativeMethods.EchoTypeExtraInfo;
        ushort ctrlScan = (ushort)NativeMethods.MapVirtualKeyW(NativeMethods.VK_CONTROL, NativeMethods.MAPVK_VK_TO_VSC);
        ushort cScan = (ushort)NativeMethods.MapVirtualKeyW(NativeMethods.VK_C, NativeMethods.MAPVK_VK_TO_VSC);
        var inputs = new NativeMethods.INPUT[4];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)NativeMethods.VK_CONTROL,
            wScan = ctrlScan,
            dwExtraInfo = extra,
        };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)NativeMethods.VK_C,
            wScan = cScan,
            dwExtraInfo = extra,
        };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)NativeMethods.VK_C,
            wScan = cScan,
            dwFlags = NativeMethods.KEYEVENTF_KEYUP,
            dwExtraInfo = extra,
        };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)NativeMethods.VK_CONTROL,
            wScan = ctrlScan,
            dwFlags = NativeMethods.KEYEVENTF_KEYUP,
            dwExtraInfo = extra,
        };
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != inputs.Length) {
            Log.Write("selection: SendInput Ctrl+C sent " + sent + "/" + inputs.Length
                + " (error " + Marshal.GetLastWin32Error() + ")");
        }
    }

    private static void RestoreClipboardText(string? saved) {
        for (int attempt = 0; attempt < 6; attempt++) {
            try {
                if (saved != null) {
                    Clipboard.SetText(saved);
                } else {
                    Clipboard.Clear();
                }
                return;
            } catch (ExternalException) {
                Thread.Sleep(40);
            } catch (Exception ex) {
                Log.Write("selection: clipboard restore failed: " + ex.Message);
                return;
            }
        }
        Log.Write("selection: clipboard restore failed: still locked");
    }

    private static bool SetClipboardText(string text) {
        for (int attempt = 0; ; attempt++) {
            try {
                Clipboard.SetText(text);
                return true;
            } catch (ExternalException) when (attempt < 6) {
                Thread.Sleep(40);
            } catch (Exception ex) {
                Log.Write("selection: clipboard set failed: " + ex.Message);
                return false;
            }
        }
    }

    private static string? TryGetClipboardText() {
        try {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        } catch {
            return null;
        }
    }
}
