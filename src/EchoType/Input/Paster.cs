using System.Runtime.InteropServices;
using System.Text;
using EchoType.Native;

namespace EchoType.Input;

/// <summary>
/// Clipboard + synthesized Ctrl+V into the focused app (Paster.swift analog).
/// Must be called from the UI (STA) thread — clipboard access and the paste timer
/// both require it.
/// </summary>
internal static class Paster {

    internal enum Outcome { Pasted, CopiedToClipboard }

    public static Outcome Deliver(string text, bool keepTranscriptOnClipboard) {
        if (!HasTextTarget()) {
            // Nowhere sensible to paste — always leave the transcript on the clipboard.
            CopyToClipboard(text);
            return Outcome.CopiedToClipboard;
        }

        if (keepTranscriptOnClipboard) {
            CopyToClipboard(text);
            SynthesizeCtrlV();
        } else {
            string? saved = TryGetClipboardText();
            CopyToClipboard(text);
            SynthesizeCtrlV();
            RestoreClipboardLater(saved, text);
        }
        return Outcome.Pasted;
    }

    /// <summary>Pastes, then restores whatever was previously on the clipboard
    /// (unless the paste itself replaced our text) — mac pasteRestoringClipboard parity.</summary>
    private static void RestoreClipboardLater(string? saved, string text) {
        var timer = new System.Windows.Forms.Timer { Interval = 700 };
        timer.Tick += (_, _) => {
            try {
                if (TryGetClipboardText() == text && saved is not null) {
                    CopyToClipboard(saved);
                }
            } finally {
                timer.Stop();
                timer.Dispose();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// MVP focus heuristic (the macOS version's AX probe equivalent is deferred):
    /// paste into anything except our own windows and known non-text shell surfaces.
    /// The macOS app's bias is to paste rather than lose the transcript, and that is
    /// preserved here.
    /// </summary>
    private static bool HasTextTarget() {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) {
            return true; // unknown focus — paste rather than lose it
        }
        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) {
            return false; // our own windows (hidden webview / login)
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(fg, className, 256);
        switch (className.ToString()) {
            case "Progman":           // desktop
            case "WorkerW":           // desktop (alternate host)
            case "Shell_TrayWnd":     // taskbar
            case "CabinetWClass":     // Explorer file window
            case "ExploreWClass":
                return false;
            default:
                return true;          // terminals, Telegram, VS Code, browsers, ...
        }
    }

    private static void SynthesizeCtrlV() {
        var inputs = new NativeMethods.INPUT[4];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_CONTROL };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_V };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static string? TryGetClipboardText() {
        try {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        } catch (Exception) {
            return null;
        }
    }

    private static void CopyToClipboard(string text) {
        // The clipboard is a shared resource; another app can hold it open briefly.
        for (int attempt = 0; ; attempt++) {
            try {
                Clipboard.SetText(text);
                return;
            } catch (ExternalException) when (attempt < 4) {
                Thread.Sleep(50);
            }
        }
    }
}
