namespace EchoType.Web;

/// <summary>
/// claude.ai DOM hooks. A Claude UI change is a one-file patch, same idea as
/// <see cref="Selectors"/> and <see cref="GeminiSelectors"/>. Built against the public
/// claude.ai markup (ProseMirror composer, <c>data-testid="user-message"</c>,
/// <c>font-claude-response</c> replies) as of 2026-10.
///
/// Voice input: click the dictation mic to start and click it again to stop. Claude
/// writes the transcript into the composer live while you speak, which EchoType
/// mirrors in the HUD (<see cref="SelectorSet.LiveTranscript"/>). The final text is
/// read from the composer once dictation stops and is NOT sent. Never click Voice
/// mode — that opens a spoken conversation instead of dictation.
/// </summary>
internal static class ClaudeSelectors {

    public static SelectorSet Claude { get; } = new() {
        Composer = [
            "div.ProseMirror[contenteditable=\"true\"]",
            "[data-testid=\"chat-input\"] [contenteditable=\"true\"]",
            "[aria-label=\"Write your prompt to Claude\"]",
            "fieldset [contenteditable=\"true\"]",
            "[contenteditable=\"true\"][role=\"textbox\"]",
        ],
        LoggedOutMarker =
            "a[href=\"/login\"], a[href^=\"/login?\"], button[data-testid=\"login-with-google\"], input#email[type=\"email\"]",
        Start = [
            "^dictat", "^start dictation", "dictation", "^microphone$", "^voice input$",
            "^start recording$", "^record$", "إملاء", "ميكروفون", "إدخال صوتي",
        ],
        StartCss = [
            "button[aria-label=\"Dictation\"]",
            "button[aria-label=\"Start dictation\"]",
            "button[aria-label=\"Dictate\"]",
            "button[aria-label*=\"dictat\" i]:not([aria-label*=\"stop\" i])",
            "button[data-testid=\"dictation-button\"]",
            "button[data-testid*=\"dictation\" i]",
        ],
        Submit = [
            "^stop dictation", "^stop recording$", "^stop listening$", "^done$",
            "finish.*dictation", "submit.*dictation", "إيقاف الإملاء", "إيقاف التسجيل",
        ],
        SubmitCss = [
            "button[aria-label=\"Stop dictation\"]",
            "button[aria-label=\"Stop recording\"]",
            "button[aria-label*=\"stop\" i][aria-label*=\"dictat\" i]",
            "button[aria-label*=\"dictat\" i][aria-pressed=\"true\"]",
            "button[data-testid*=\"dictation\" i][aria-pressed=\"true\"]",
        ],
        Cancel = ["^cancel dictation", "^discard", "^cancel$"],
        NeverClick = [
            "voice mode", "^start voice", "^use voice", "voice conversation", "^send message$",
        ],
        Send = ["^send message$", "^send$", "^إرسال"],
        SendCss = [
            "button[aria-label=\"Send message\"]",
            "button[aria-label=\"Send Message\"]",
            "button[data-testid=\"send-button\"]",
            "fieldset button[type=\"submit\"]",
        ],
        Stop = ["^stop response$", "^stop generating$", "^stop$", "^إيقاف"],
        StopCss = [
            "button[aria-label=\"Stop response\"]",
            "button[aria-label=\"Stop Response\"]",
            "button[data-testid=\"stop-button\"]",
        ],
        NewChat = ["^new chat$", "^start new chat$", "^محادثة جديدة"],
        NewChatCss = [
            "a[href=\"/new\"]",
            "a[aria-label=\"New chat\"]",
            "button[aria-label=\"New chat\"]",
            "[data-testid=\"new-chat-button\"]",
        ],
        OpenSidebar = ["^open sidebar$", "^sidebar$", "^menu$"],
        OpenSidebarCss = [
            "button[aria-label=\"Open sidebar\"]",
            "button[data-testid=\"pin-sidebar-toggle\"]",
        ],
        DictatingCss = [
            "button[aria-label=\"Stop dictation\"]",
            "button[aria-label*=\"stop\" i][aria-label*=\"dictat\" i]",
            "button[aria-label*=\"dictat\" i][aria-pressed=\"true\"]",
            "button[data-testid*=\"dictation\" i][aria-pressed=\"true\"]",
        ],
        GeneratingCss = [
            "[data-is-streaming=\"true\"]",
            "button[aria-label=\"Stop response\"]",
        ],
        Assistant = [
            "[data-is-streaming]",
            ".font-claude-response",
            ".font-claude-message",
            "[data-testid=\"assistant-message\"]",
        ],
        AssistantMarkdown = [
            ".font-claude-response",
            ".font-claude-message",
            ".standard-markdown",
            ".progressive-markdown",
            ".markdown",
        ],
        User = [
            "[data-testid=\"user-message\"]",
            ".font-user-message",
        ],
        HasDictationShortcut = false,
        HasDictationSubmitShortcut = false,
        SubmitFallsBackToStart = true,
        TreatGumAsEngaged = true,
        UseUserMessageAsTranscriptFallback = true,
        UseAccessibilityTranscriptFallback = false,
        LiveTranscript = true,
        // Interim words are rewritten for a moment after the mic closes; wait ~1.5 s
        // of unchanged composer text before taking the final transcript.
        TranscriptSettlePolls = 6,
    };
}
