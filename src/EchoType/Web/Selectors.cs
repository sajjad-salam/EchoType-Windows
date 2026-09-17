namespace EchoType.Web;

/// <summary>
/// Every chatgpt.com DOM hook lives here so a ChatGPT UI change is a one-file patch
/// (Selectors.swift analog).
///
/// Button lookups are ordered lists of case-insensitive regex patterns matched against
/// a button's `aria-label` and `data-testid`. The first pattern with a match wins.
///
/// Verified against chatgpt.com on 2026-06-07 with a LOGGED-IN session (mac port):
///   - composer:        div#prompt-textarea (contenteditable ProseMirror)
///                      REMOVED from the DOM while dictation is active
///   - start dictation: button[aria-label="Start dictation"]
///   - while dictating: button[aria-label="Cancel dictation"], button[aria-label="Submit dictation"]
///   - send:            button[aria-label="Send prompt"] / data-testid=send-button
///                      NEVER clicked during dictation; custom commands click it after filling the composer
///   - voice mode:      button[data-testid="composer-speech-button"]  (NEVER click — opens voice chat)
///   - logged out:      button[data-testid="login-button"] present
/// Submit dictation inserts the transcript into the composer (~0.5 s) and does NOT send the
/// message. Custom commands then optionally send that text (plus a prompt) and scrape the reply.
/// `__echotype.dump()` (see DictationDriver.DumpButtonsAsync) logs the real buttons at runtime
/// so a mismatch is a quick patch (check the log).
/// </summary>
internal static class Selectors {

    /// <summary>CSS selectors tried in order for the composer text area.</summary>
    public static readonly string[] Composer = {
        "#prompt-textarea",
        "div#prompt-textarea.ProseMirror",
        "[data-testid=\"prompt-textarea\"]",
        "[data-lexical-editor=\"true\"]",
        "div.ProseMirror[contenteditable=\"true\"]",
        "div.ProseMirror[contenteditable=\"plaintext-only\"]",
        "[contenteditable=\"plaintext-only\"]",
        "form [contenteditable=\"true\"]",
        "form textarea",
        "[role=\"textbox\"][contenteditable]",
        "textarea[data-id=\"root\"]",
        "[data-virtualkeyboard=\"true\"][contenteditable]",
    };

    /// <summary>CSS selector whose presence means the session is logged OUT.</summary>
    public const string LoggedOutMarker = "[data-testid=\"login-button\"]";

    /// <summary>Idle composer: the mic button that starts dictation.</summary>
    public static readonly string[] StartDictation = { "^start dictation$", "^dictate", "dictation" };

    public static readonly string[] StartDictationCss = {
        "button[aria-label=\"Start dictation\"]",
    };

    /// <summary>While dictating: the ✓ button that stops listening and transcribes.</summary>
    public static readonly string[] SubmitDictation = { "^submit dictation$", "submit.*dictation", "finish.*dictation", "^done$" };

    public static readonly string[] SubmitDictationCss = {
        "button[aria-label=\"Submit dictation\"]",
    };

    /// <summary>While dictating: the ✕ button that discards the dictation.</summary>
    public static readonly string[] CancelDictation = { "^cancel dictation$", "cancel.*dictation", "stop.*dictation", "discard.*dictation" };

    /// <summary>Safety only — patterns we must NEVER click during dictation (send / voice chat).</summary>
    public static readonly string[] NeverClick = { "^send prompt$", "send-button", "composer-submit-button", "composer-speech-button", "start voice" };

    /// <summary>Idle composer: send the prompt (custom-command path only).</summary>
    public static readonly string[] SendPrompt = { "^send prompt$", "^send$", "send-button", "composer-submit-button" };

    public static readonly string[] SendPromptCss = {
        "#composer-submit-button",
        "button[data-testid=\"send-button\"]",
        "button[aria-label=\"Send prompt\"]",
    };

    /// <summary>While ChatGPT is writing a reply.</summary>
    public static readonly string[] StopGenerating = { "^stop streaming$", "^stop generating$", "stop-button" };

    public static readonly string[] StopGeneratingCss = {
        "button[data-testid=\"stop-button\"]",
        "button[aria-label=\"Stop streaming\"]",
        "button[aria-label=\"Stop generating\"]",
    };

    public static readonly string[] NewChat = { "^new chat$", "create-new-chat" };

    public static readonly string[] NewChatCss = {
        "[data-testid=\"create-new-chat-button\"]",
        "a[aria-label=\"New chat\"]",
        "button[aria-label=\"New chat\"]",
    };

    public static readonly string[] OpenSidebar = { "^open sidebar$" };

    public static readonly string[] OpenSidebarCss = {
        "button[aria-label=\"Open sidebar\"]",
    };

    public static readonly string[] Assistant = {
        "[data-message-author-role=\"assistant\"]",
        "article[data-turn=\"assistant\"]",
        "[data-turn=\"assistant\"]",
        "[data-testid=\"assistant-message\"]",
        "[data-testid^=\"conversation-turn\"][data-turn=\"assistant\"]",
    };

    public static readonly string[] AssistantMarkdown = {
        ".markdown", ".prose", "[data-testid=\"markdown\"]", ".whitespace-pre-wrap",
    };

    public static readonly string[] User = {
        "[data-message-author-role=\"user\"]",
        "article[data-turn=\"user\"]",
        "[data-turn=\"user\"]",
        "[data-testid=\"user-message\"]",
        "[data-testid^=\"conversation-turn\"][data-turn=\"user\"]",
    };

    public static SelectorSet ChatGpt { get; } = new() {
        Composer = Composer,
        LoggedOutMarker = LoggedOutMarker,
        Start = StartDictation,
        StartCss = StartDictationCss,
        Submit = SubmitDictation,
        SubmitCss = SubmitDictationCss,
        Cancel = CancelDictation,
        NeverClick = NeverClick,
        Send = SendPrompt,
        SendCss = SendPromptCss,
        Stop = StopGenerating,
        StopCss = StopGeneratingCss,
        NewChat = NewChat,
        NewChatCss = NewChatCss,
        OpenSidebar = OpenSidebar,
        OpenSidebarCss = OpenSidebarCss,
        DictatingCss = [
            "button[aria-label=\"Submit dictation\"]",
            "button[aria-label=\"Cancel dictation\"]",
        ],
        GeneratingCss = [".result-streaming", "[data-testid=\"stop-button\"]"],
        Assistant = Assistant,
        AssistantMarkdown = AssistantMarkdown,
        User = User,
        HasDictationShortcut = true,
        HasDictationSubmitShortcut = true,
        SubmitFallsBackToStart = false,
        TreatGumAsEngaged = false,
        UseUserMessageAsTranscriptFallback = true,
        UseAccessibilityTranscriptFallback = true,
    };
}
