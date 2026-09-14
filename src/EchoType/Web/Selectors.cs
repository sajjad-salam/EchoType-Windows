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
///   - send (never!):   button[aria-label="Send prompt"]
///   - voice mode:      button[data-testid="composer-speech-button"]  (NEVER click — opens voice chat)
///   - logged out:      button[data-testid="login-button"] present
/// Submit inserts the transcript into the composer (~0.5 s) and does NOT send the
/// message. `__echotype.dump()` (see DictationDriver.DumpButtonsAsync) logs the real
/// buttons at runtime so a mismatch is a quick patch (check the log).
/// </summary>
internal static class Selectors {

    /// <summary>CSS selectors tried in order for the composer text area.</summary>
    public static readonly string[] Composer = { "#prompt-textarea", "div.ProseMirror[contenteditable=\"true\"]" };

    /// <summary>CSS selector whose presence means the session is logged OUT.</summary>
    public const string LoggedOutMarker = "[data-testid=\"login-button\"]";

    /// <summary>Idle composer: the mic button that starts dictation.</summary>
    public static readonly string[] StartDictation = { "^start dictation$", "^dictate", "dictation" };

    /// <summary>While dictating: the ✓ button that stops listening and transcribes.</summary>
    public static readonly string[] SubmitDictation = { "^submit dictation$", "submit.*dictation", "finish.*dictation", "^done$" };

    /// <summary>While dictating: the ✕ button that discards the dictation.</summary>
    public static readonly string[] CancelDictation = { "^cancel dictation$", "cancel.*dictation", "stop.*dictation", "discard.*dictation" };

    /// <summary>Safety only — patterns we must NEVER click (send message / open voice chat).</summary>
    public static readonly string[] NeverClick = { "^send prompt$", "send-button", "composer-speech-button", "start voice" };

    /// <summary>Renders the pattern lists as a JS object literal for injection.</summary>
    public static string Js() {
        return "{"
            + "composer:" + Arr(Composer)
            + ",loggedOutMarker:" + Str(LoggedOutMarker)
            + ",start:" + Arr(StartDictation)
            + ",submit:" + Arr(SubmitDictation)
            + ",cancel:" + Arr(CancelDictation)
            + ",neverClick:" + Arr(NeverClick)
            + "}";
    }

    private static string Arr(string[] patterns) => "[" + string.Join(",", patterns.Select(Str)) + "]";

    private static string Str(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
