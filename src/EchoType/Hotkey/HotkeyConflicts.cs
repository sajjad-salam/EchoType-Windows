using EchoType.Native;

namespace EchoType.Hotkey;

/// <summary>Shared collision checks for reserved hold and tap keys.</summary>
internal static class HotkeyConflicts {

    public static string? Message(int vk, Settings settings, params int[] ignoreVks) {
        if (vk <= 0) {
            return null;
        }
        if (vk == (int)NativeMethods.VK_RETURN) {
            return "Enter cannot be reserved — EchoType would swallow every Enter key.";
        }
        if (IsReserved(vk, settings.HotkeyVk, ignoreVks)) {
            return $"That key is already the dictation shortcut ({HotkeyNames.For(settings.HotkeyVk)}).";
        }
        if (IsReserved(vk, settings.PressEnterToggleVk, ignoreVks)) {
            return "That key is already the Auto Enter shortcut.";
        }
        if (IsReserved(vk, settings.ChatGptSwitchVk, ignoreVks)) {
            return "That key already switches to ChatGPT.";
        }
        if (IsReserved(vk, settings.GeminiSwitchVk, ignoreVks)) {
            return "That key already switches to Gemini.";
        }
        if (IsReserved(vk, settings.AskModelVk, ignoreVks)) {
            return "That key is already the Ask model shortcut.";
        }
        if (IsReserved(vk, settings.OpenModelWindowVk, ignoreVks)) {
            return "That key is already the model window shortcut.";
        }
        if (settings.ActiveCommands.Any(c => c.HotkeyVk == vk && !ignoreVks.Contains(vk))) {
            return "A custom command already uses that key.";
        }
        return null;
    }

    private static bool IsReserved(int vk, int reserved, int[] ignoreVks) =>
        reserved != 0 && vk == reserved && !ignoreVks.Contains(vk);
}
