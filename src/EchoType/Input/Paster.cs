using System.Drawing.Imaging;
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

    public static Outcome Deliver(
        string text,
        bool keepTranscriptOnClipboard,
        bool pressEnterAfterPaste,
        PasteTarget? target = null) {

        if (target != null) {
            if (!target.Restore()) {
                CopyToClipboard(text);
                return Outcome.CopiedToClipboard;
            }
            // Give the restored app a beat to accept input after a desktop/window switch.
            Thread.Sleep(80);
        } else if (!HasTextTarget()) {
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
        if (pressEnterAfterPaste) {
            // Give the target app a beat to apply the paste before submitting.
            Thread.Sleep(120);
            SynthesizeEnter();
        }
        return Outcome.Pasted;
    }

    /// <summary>
    /// Puts an image on the clipboard and pastes it into the captured target.
    /// The image stays on the clipboard so the user can paste it again.
    /// </summary>
    public static Outcome DeliverImage(
        Image image,
        bool pressEnterAfterPaste,
        PasteTarget? target = null) {

        if (target != null) {
            if (!target.Restore()) {
                CopyImageToClipboard(image);
                return Outcome.CopiedToClipboard;
            }
            Thread.Sleep(80);
        } else if (!HasTextTarget()) {
            CopyImageToClipboard(image);
            return Outcome.CopiedToClipboard;
        }

        CopyImageToClipboard(image);
        SynthesizeCtrlV();
        if (pressEnterAfterPaste) {
            Thread.Sleep(120);
            SynthesizeEnter();
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
    internal static bool LooksLikeTextTarget(IntPtr hwnd) {
        if (hwnd == IntPtr.Zero) {
            return true; // unknown focus — paste rather than lose it
        }
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) {
            return false; // our own windows (hidden webview / login)
        }

        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, className, 256);
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

    private static bool HasTextTarget() => LooksLikeTextTarget(NativeMethods.GetForegroundWindow());

    private static void SynthesizeCtrlV() {
        var extra = NativeMethods.EchoTypeExtraInfo;
        var inputs = new NativeMethods.INPUT[4];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_CONTROL, dwExtraInfo = extra };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_V, dwExtraInfo = extra };
        inputs[2].type = NativeMethods.INPUT_KEYBOARD;
        inputs[2].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_V, dwFlags = NativeMethods.KEYEVENTF_KEYUP, dwExtraInfo = extra };
        inputs[3].type = NativeMethods.INPUT_KEYBOARD;
        inputs[3].U.ki = new NativeMethods.KEYBDINPUT { wVk = (ushort)NativeMethods.VK_CONTROL, dwFlags = NativeMethods.KEYEVENTF_KEYUP, dwExtraInfo = extra };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void SynthesizeEnter() {
        var extra = NativeMethods.EchoTypeExtraInfo;
        ushort scan = (ushort)NativeMethods.MapVirtualKeyW(NativeMethods.VK_RETURN, NativeMethods.MAPVK_VK_TO_VSC);
        var inputs = new NativeMethods.INPUT[2];
        inputs[0].type = NativeMethods.INPUT_KEYBOARD;
        inputs[0].U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)NativeMethods.VK_RETURN,
            wScan = scan,
            dwExtraInfo = extra,
        };
        inputs[1].type = NativeMethods.INPUT_KEYBOARD;
        inputs[1].U.ki = new NativeMethods.KEYBDINPUT {
            wVk = (ushort)NativeMethods.VK_RETURN,
            wScan = scan,
            dwFlags = NativeMethods.KEYEVENTF_KEYUP,
            dwExtraInfo = extra,
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static string? TryGetClipboardText() {
        try {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        } catch (Exception) {
            return null;
        }
    }

    private static void CopyImageToClipboard(Image image) {
        using var png = new MemoryStream();
        image.Save(png, ImageFormat.Png);
        png.Position = 0;
        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", false, png.ToArray());
        for (int attempt = 0; ; attempt++) {
            try {
                Clipboard.SetDataObject(data, true);
                return;
            } catch (ExternalException) when (attempt < 4) {
                Thread.Sleep(50);
            }
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
