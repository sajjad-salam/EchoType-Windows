namespace EchoType;

/// <summary>Dictation flow state (AppPhase in AppDelegate.swift; no hands-free/pendingDoubleTap in MVP).</summary>
internal enum AppPhase {
    Idle,
    Waking,        // webview not loaded yet — "Waking up…"
    Engaging,      // webview loaded, starting dictation
    Listening,     // mic open
    Transcribing,  // submitted, waiting for text
}
