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

    internal readonly struct DeliverResult {
        public Outcome Outcome { get; }
        /// <summary>
        /// True when the user had switched away and was left on that window
        /// (background paste, or focus handed back after Ctrl+V).
        /// </summary>
        public bool KeptUserFocus { get; }

        public DeliverResult(Outcome outcome, bool keptUserFocus = false) {
            Outcome = outcome;
            KeptUserFocus = keptUserFocus;
        }

        public static DeliverResult Copied() => new(Outcome.CopiedToClipboard);
        public static DeliverResult Pasted(bool keptUserFocus) => new(Outcome.Pasted, keptUserFocus);
    }

    /// <summary>Puts text on the clipboard without pasting. Used to keep a spoken
    /// transcript if a model reply never arrives.</summary>
    public static void Copy(string text) => CopyToClipboard(text);

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard after a short delay so a
    /// Ctrl+V already in flight can still read whatever we just pasted. Qt and
    /// Electron apps often read the clipboard on the next event-loop tick.
    /// </summary>
    public static void CopyLater(string text, int delayMs = 1000) {
        if (string.IsNullOrEmpty(text)) {
            return;
        }
        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(delayMs, 200) };
        timer.Tick += (_, _) => {
            try {
                CopyToClipboard(text);
                Log.Write("clipboard: copied transcript after paste (" + text.Length + " chars)");
            } catch (Exception ex) {
                Log.Write("clipboard: delayed transcript copy failed: " + ex.Message);
            } finally {
                timer.Stop();
                timer.Dispose();
            }
        };
        timer.Start();
    }

    public static DeliverResult Deliver(
        string text,
        bool keepTranscriptOnClipboard,
        bool pressEnterAfterPaste,
        PasteTarget? target = null) {

        PasteTarget? resume = CaptureResume(target);

        if (target != null
            && resume != null
            && !pressEnterAfterPaste
            && target.AcceptsBackgroundPaste) {
            string? saved = keepTranscriptOnClipboard ? null : TryGetClipboardText();
            CopyToClipboard(text);
            if (target.TryBackgroundPaste()) {
                if (keepTranscriptOnClipboard) {
                    Thread.Sleep(80);
                } else {
                    RestoreClipboardLater(saved, text);
                }
                return DeliverResult.Pasted(keptUserFocus: true);
            }
        }

        if (target != null) {
            if (!target.Restore()) {
                CopyToClipboard(text);
                return DeliverResult.Copied();
            }
            // Give the restored app a beat to accept input after a desktop/window switch.
            Thread.Sleep(80);
        } else if (!HasTextTarget()) {
            // Nowhere sensible to paste — always leave the transcript on the clipboard.
            CopyToClipboard(text);
            return DeliverResult.Copied();
        }

        if (keepTranscriptOnClipboard) {
            CopyToClipboard(text);
            SynthesizeCtrlV();
            // Let the target read the clipboard before the caller replaces it.
            Thread.Sleep(80);
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

        return HandBackFocus(resume);
    }

    /// <summary>
    /// Puts an image on the clipboard and pastes it into the captured target.
    /// The image stays on the clipboard so the user can paste it again.
    /// </summary>
    public static DeliverResult DeliverImage(
        Image image,
        bool pressEnterAfterPaste,
        PasteTarget? target = null) {

        PasteTarget? resume = CaptureResume(target);

        if (target != null) {
            if (!target.Restore()) {
                CopyImageToClipboard(image);
                return DeliverResult.Copied();
            }
            Thread.Sleep(80);
        } else if (!HasTextTarget()) {
            CopyImageToClipboard(image);
            return DeliverResult.Copied();
        }

        CopyImageToClipboard(image);
        SynthesizeCtrlV();
        if (pressEnterAfterPaste) {
            Thread.Sleep(120);
            SynthesizeEnter();
        }

        return HandBackFocus(resume);
    }

    private static PasteTarget? CaptureResume(PasteTarget? target) =>
        target == null ? null : PasteTarget.CaptureForegroundExcept(target);

    /// <summary>
    /// After Ctrl+V, put the user back on the window they were reading. Windows
    /// cannot send keystrokes to a background Chromium/Qt field, so this is a
    /// brief steal-and-restore rather than a true background paste.
    /// </summary>
    private static DeliverResult HandBackFocus(PasteTarget? resume) {
        if (resume == null) {
            return DeliverResult.Pasted(keptUserFocus: false);
        }
        Thread.Sleep(80);
        resume.Restore(required: false);
        return DeliverResult.Pasted(keptUserFocus: true);
    }

    /// <summary>Pastes, then restores whatever was previously on the clipboard
    /// (unless the paste itself replaced our text) — mac pasteRestoringClipboard parity.</summary>
    private static void RestoreClipboardLater(string? saved, string text) {
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
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
