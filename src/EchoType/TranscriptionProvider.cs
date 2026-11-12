namespace EchoType;

/// <summary>Web dictation backend. The tray menu picks one; custom commands follow it.</summary>
internal enum TranscriptionProvider {
    ChatGpt,
    Gemini,
    Claude,
}

internal static class TranscriptionProviders {
    /// <summary>
    /// ChatGPT web is not used for dictation: EchoType's click on its mic opens the stream but
    /// ChatGPT never records, so every transcript came back empty. It still serves Clean, Ask,
    /// custom commands and translation.
    /// </summary>
    public static bool CanTranscribe(this TranscriptionProvider provider) => provider != TranscriptionProvider.ChatGpt;

    /// <summary>Models EchoType may dictate with, in race/menu order.</summary>
    public static IEnumerable<TranscriptionProvider> Transcribing =>
        Enum.GetValues<TranscriptionProvider>().Where(CanTranscribe);
}
