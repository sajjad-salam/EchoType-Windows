namespace EchoType.Web;

/// <summary>
/// DOM hooks for one chat site. Rendered into the injected <c>__echotype</c> script
/// so ChatGPT and Gemini can share DictationDriver.
/// </summary>
internal sealed class SelectorSet {

    public required string[] Composer { get; init; }
    public required string LoggedOutMarker { get; init; }
    public required string[] Start { get; init; }
    public string[] StartCss { get; init; } = [];
    public required string[] Submit { get; init; }
    public string[] SubmitCss { get; init; } = [];
    public required string[] Cancel { get; init; }
    public string[] CancelCss { get; init; } = [];
    public required string[] NeverClick { get; init; }
    public required string[] Send { get; init; }
    public required string[] SendCss { get; init; }
    public required string[] Stop { get; init; }
    public required string[] StopCss { get; init; }
    public required string[] NewChat { get; init; }
    public required string[] NewChatCss { get; init; }
    public string[] OpenSidebar { get; init; } = [];
    public string[] OpenSidebarCss { get; init; } = [];
    public string[] DictatingCss { get; init; } = [];
    public string[] GeneratingCss { get; init; } = [];
    public required string[] Assistant { get; init; }
    public required string[] AssistantMarkdown { get; init; }
    public required string[] User { get; init; }

    /// <summary>ChatGPT toggles dictation start with Ctrl+Shift+D.</summary>
    public bool HasDictationShortcut { get; init; }

    /// <summary>
    /// Send Ctrl+Shift+D to stop/submit dictation. ChatGPT uses this as a toggle;
    /// Gemini's voice input stops on the same shortcut even when start was a mic click.
    /// </summary>
    public bool HasDictationSubmitShortcut { get; init; }

    /// <summary>
    /// Gemini's mic is also the stop control. ChatGPT must not click Start as Submit —
    /// after the overlay closes that click restarts dictation or hits Send.
    /// </summary>
    public bool SubmitFallsBackToStart { get; init; }

    /// <summary>
    /// Gemini's mic often has no separate "submit dictation" control. After we click
    /// it, getUserMedia requested/ok is enough to count as listening.
    /// </summary>
    public bool TreatGumAsEngaged { get; init; }

    /// <summary>
    /// If voice input auto-sends, scrape the last user bubble instead of the composer.
    /// </summary>
    public bool UseUserMessageAsTranscriptFallback { get; init; }

    /// <summary>
    /// ChatGPT can hide the transcript in a closed shadow tree; read it from the
    /// accessibility tree while waiting. Gemini's Quill composer is DOM-readable —
    /// using AX there copies conversation titles from the sidebar/header.
    /// </summary>
    public bool UseAccessibilityTranscriptFallback { get; init; }

    public string Js() {
        return "{"
            + "composer:" + Arr(Composer)
            + ",loggedOutMarker:" + Str(LoggedOutMarker)
            + ",start:" + Arr(Start)
            + ",startCss:" + Arr(StartCss)
            + ",submit:" + Arr(Submit)
            + ",submitCss:" + Arr(SubmitCss)
            + ",cancel:" + Arr(Cancel)
            + ",cancelCss:" + Arr(CancelCss)
            + ",neverClick:" + Arr(NeverClick)
            + ",send:" + Arr(Send)
            + ",sendCss:" + Arr(SendCss)
            + ",stop:" + Arr(Stop)
            + ",stopCss:" + Arr(StopCss)
            + ",newChat:" + Arr(NewChat)
            + ",newChatCss:" + Arr(NewChatCss)
            + ",openSidebar:" + Arr(OpenSidebar)
            + ",openSidebarCss:" + Arr(OpenSidebarCss)
            + ",dictatingCss:" + Arr(DictatingCss)
            + ",generatingCss:" + Arr(GeneratingCss)
            + ",assistant:" + Arr(Assistant)
            + ",assistantMarkdown:" + Arr(AssistantMarkdown)
            + ",user:" + Arr(User)
            + ",treatGumAsEngaged:" + (TreatGumAsEngaged ? "true" : "false")
            + ",submitFallsBackToStart:" + (SubmitFallsBackToStart ? "true" : "false")
            + "}";
    }

    private static string Arr(string[] patterns) => "[" + string.Join(",", patterns.Select(Str)) + "]";

    private static string Str(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
