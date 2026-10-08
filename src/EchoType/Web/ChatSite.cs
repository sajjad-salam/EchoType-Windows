namespace EchoType.Web;

/// <summary>Which chat page the hidden WebView2 loads, plus its DOM selectors.</summary>
internal sealed class ChatSite {

    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ChatUrl { get; init; }
    public required string ProfileFolder { get; init; }
    public required string LoginTitle { get; init; }
    public required SelectorSet SelectorSet { get; init; }
    public required string[] MicHosts { get; init; }
    public required string[] StayOnHosts { get; init; }

    public static ChatSite ChatGpt { get; } = new() {
        Id = "chatgpt",
        DisplayName = "ChatGPT",
        ChatUrl = "https://chatgpt.com/",
        ProfileFolder = "WebView2",
        LoginTitle = "EchoType — ChatGPT Login",
        SelectorSet = Selectors.ChatGpt,
        MicHosts = ["chatgpt.com", "openai.com"],
        StayOnHosts = [
            "chatgpt.com", "openai.com", "google.com", "microsoftonline.com",
            "apple.com", "live.com",
        ],
    };

    public static ChatSite Gemini { get; } = new() {
        Id = "gemini",
        DisplayName = "Gemini",
        ChatUrl = "https://gemini.google.com/app",
        ProfileFolder = "WebView2-Gemini",
        LoginTitle = "EchoType — Gemini Login",
        SelectorSet = GeminiSelectors.Gemini,
        MicHosts = ["gemini.google.com", "google.com", "bard.google.com"],
        StayOnHosts = [
            "gemini.google.com", "bard.google.com", "google.com", "youtube.com",
            "gstatic.com", "googleapis.com",
        ],
    };

    public static ChatSite Claude { get; } = new() {
        Id = "claude",
        DisplayName = "Claude",
        ChatUrl = "https://claude.ai/new",
        ProfileFolder = "WebView2-Claude",
        LoginTitle = "EchoType — Claude Login",
        SelectorSet = ClaudeSelectors.Claude,
        MicHosts = ["claude.ai", "anthropic.com"],
        StayOnHosts = [
            "claude.ai", "anthropic.com", "claudeusercontent.com", "google.com",
            "accounts.google.com", "apple.com", "gstatic.com", "googleapis.com",
        ],
    };

    public static ChatSite For(TranscriptionProvider provider) => provider switch {
        TranscriptionProvider.Gemini => Gemini,
        TranscriptionProvider.Claude => Claude,
        _ => ChatGpt,
    };
}
