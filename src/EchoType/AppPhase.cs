namespace EchoType;

/// <summary>Dictation flow state (AppPhase in AppDelegate.swift).</summary>
internal enum AppPhase {
    Idle,
    Waking,        // webview not loaded yet — "Waking up…"
    Engaging,      // webview loaded, starting dictation
    Listening,     // mic open
    Transcribing,  // submitted, waiting for text
    ChoosingAction, // custom command with several buttons: waiting for the user to pick one
    Generating,    // custom command, ask-model, or selection-edit: waiting for the model's reply
}
