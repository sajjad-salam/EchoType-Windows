namespace EchoType.Web;

/// <summary>
/// gemini.google.com DOM hooks. A Gemini UI change is a one-file patch, same idea as
/// <see cref="Selectors"/>. Verified against public Gemini web markup (Quill composer,
/// <c>speech_dictation_mic_button</c>, <c>user-query</c> / <c>model-response</c>) as of 2026-09.
///
/// Voice input: click the mic to start. Ctrl+Shift+D (same as ChatGPT) stops it —
/// clicking the mic again is unreliable, especially on non-English UI. The transcript
/// lands in the Quill composer and should NOT be sent. If Gemini auto-sends on silence,
/// EchoType alerts that recording stopped, scrapes the last <c>user-query</c> bubble
/// instead, and stops the unwanted reply.
/// Do not read the accessibility tree for the transcript — Gemini exposes conversation
/// titles as textboxes, and that used to get pasted while speech-to-text was still running.
/// </summary>
internal static class GeminiSelectors {

    public static SelectorSet Gemini { get; } = new() {
        Composer = [
            "rich-textarea .ql-editor[contenteditable=\"true\"]",
            ".ql-editor[contenteditable=\"true\"]",
            "[aria-label=\"Enter a prompt for Gemini\"]",
            "[contenteditable=\"true\"][role=\"textbox\"]",
        ],
        LoggedOutMarker =
            "a[data-test-id=\"sign-in-button\"], button[data-test-id=\"sign-in-button\"], a[aria-label=\"Sign in\"]",
        Start = [
            "^microphone$", "^voice input$", "^start voice", "dictate", "dictation", "^mic$",
            "^استماع$", "ميكروفون", "إدخال صوتي", "إدخال بالصوت",
        ],
        StartCss = [
            "button[data-node-type=\"speech_dictation_mic_button\"]",
            "speech-dictation-mic-button button",
            "button[aria-label=\"Microphone\"]",
            "button[aria-label=\"Voice input\"]",
            "button[aria-label=\"استماع\"]",
        ],
        Submit = [
            "^stop listening$", "^stop recording$", "^stop voice", "^done$",
            "submit.*dictation", "finish.*dictation",
            "إيقاف الاستماع", "إيقاف التسجيل", "إيقاف الإدخال",
        ],
        SubmitCss = [
            "button[data-node-type=\"speech_dictation_mic_button\"][aria-pressed=\"true\"]",
            "speech-dictation-mic-button[listening] button",
            "speech-dictation-mic-button[recording] button",
            "button[aria-label=\"Stop listening\"]",
            "button[aria-label=\"Stop recording\"]",
            "button[aria-label=\"Stop voice input\"]",
        ],
        Cancel = ["^cancel$", "^discard$", "cancel.*dictation", "stop.*dictation"],
        NeverClick = [
            "^send message$", "send-button", "start voice call", "gemini live", "^live$",
        ],
        Send = ["^send message$", "^send$", "^submit$", "^إرسال", "ارسال"],
        SendCss = [
            "button.send-button[aria-label=\"Send message\"]",
            "button[aria-label=\"Send message\"]",
            "button[aria-label=\"إرسال رسالة\"]",
            "button[aria-label=\"إرسال\"]",
            "button.send-button",
            "button[data-test-id=\"send-button\"]",
            "[data-test-id=\"send-button\"]",
        ],
        Stop = [
            "^stop generating$", "^stop responding$", "^stop streaming$",
            "^إيقاف الإنشاء", "^إيقاف التوليد", "^إيقاف الاستجابة",
        ],
        StopCss = [
            "button[aria-label=\"Stop responding\"]",
            "button[aria-label=\"Stop generating\"]",
            "button[aria-label=\"Stop streaming\"]",
            "button[aria-label=\"إيقاف الإنشاء\"]",
            "button[data-test-id=\"stop-button\"]",
        ],
        NewChat = [
            "^new chat$", "^new conversation$", "^start a new chat$", "new-chat-button",
            "^محادثة جديدة", "^دردشة جديدة", "^بدء محادثة",
        ],
        NewChatCss = [
            "[data-test-id=\"new-chat-button\"]",
            "a[data-test-id=\"new-chat-button\"]",
            "button[data-test-id=\"new-chat-button\"]",
            "[data-test-id=\"bard-new-chat-button\"]",
            "button[aria-label=\"New chat\"]",
            "a[aria-label=\"New chat\"]",
            "button[aria-label=\"New conversation\"]",
            "a[aria-label=\"New conversation\"]",
            "button[aria-label=\"Start a new chat\"]",
            "button[aria-label=\"محادثة جديدة\"]",
            "a[aria-label=\"محادثة جديدة\"]",
            "button[aria-label=\"دردشة جديدة\"]",
            "a[aria-label=\"دردشة جديدة\"]",
            "button[aria-label=\"بدء محادثة جديدة\"]",
        ],
        OpenSidebar = [
            "^main menu$", "^open menu$", "^open sidebar$", "^show sidebar$",
            "^القائمة الرئيسية$", "^القائمة$",
        ],
        OpenSidebarCss = [
            "button[aria-label=\"Main menu\"]",
            "button[aria-label=\"Open menu\"]",
            "button[aria-label=\"Show side navigation\"]",
            "button[aria-label=\"Open sidebar\"]",
            "[data-test-id=\"side-nav-menu-button\"]",
            "button[aria-label=\"القائمة الرئيسية\"]",
        ],
        DictatingCss = [
            "button[data-node-type=\"speech_dictation_mic_button\"][aria-pressed=\"true\"]",
            "speech-dictation-mic-button[listening]",
            "speech-dictation-mic-button[recording]",
            "button[aria-label=\"Stop listening\"]",
            "button[aria-label=\"Stop recording\"]",
        ],
        GeneratingCss = [
            "div[class*=\"avatar_spinner_animation\"]",
            "model-response [role=\"progressbar\"]",
            "[data-test-id=\"stop-button\"]",
        ],
        Assistant = [
            "model-response",
            ".presented-response-container",
            "[data-message-author=\"model\"]",
        ],
        AssistantMarkdown = [
            "message-content",
            ".markdown.markdown-main-panel",
            ".markdown",
            ".model-response-text",
        ],
        User = [
            "user-query",
            ".user-query-bubble-with-background",
            ".query-text",
            ".query-content",
        ],
        HasDictationShortcut = false,
        HasDictationSubmitShortcut = true,
        SubmitFallsBackToStart = true,
        TreatGumAsEngaged = true,
        UseUserMessageAsTranscriptFallback = true,
        UseAccessibilityTranscriptFallback = false,
    };
}
