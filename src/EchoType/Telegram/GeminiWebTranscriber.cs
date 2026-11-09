using EchoType.Ui;
using EchoType.Web;

namespace EchoType.Telegram;

/// <summary>
/// Turns a Telegram voice note into text with Gemini web (gemini.google.com, the same
/// signed-in profile as the Gemini model in the tray): the audio is attached to a new
/// chat in a hidden window with a transcribe-only prompt, and the reply is the text.
/// No API key. Every call must run on the UI thread (WebView2).
/// </summary>
internal sealed class GeminiWebTranscriber : IDisposable {

    private const int MaxAttempts = 3;

    /// <summary>
    /// How long a sent voice note may sit with no sign of an answer before the send counts
    /// as dropped and is retried. Gemini opens the chat (/app/&lt;id&gt;) or starts
    /// generating within seconds when it really got the prompt.
    /// </summary>
    private static readonly TimeSpan ReplyStartTimeout = TimeSpan.FromSeconds(35);

    /// <summary>Once Gemini is answering, a long voice note can take a while to finish.</summary>
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(120);

    private const string TranscribePrompt =
        "Transcribe the attached audio exactly as spoken. Output ONLY the spoken words: no introduction, no comments, "
        + "no timestamps, no speaker labels, no quotes, no translation and no summary. Keep the original language "
        + "and dialect (for example Iraqi Arabic), and write English words and names in Latin letters. "
        + "If nothing intelligible is said, output nothing.";

    /// <summary>A text follow-up can take longer than a transcript (long rewrites).</summary>
    private static readonly TimeSpan FollowUpReplyTimeout = TimeSpan.FromSeconds(90);

    private ChatGPTWebController? _web;

    /// <summary>
    /// True once a voice note was transcribed in the hidden window's current chat, so a
    /// text message continues that chat (Gemini sees the transcript) instead of a new one.
    /// </summary>
    private bool _hasConversation;

    public void Dispose() {
        _web?.Dispose();
        _web = null;
    }

    /// <summary>Speech → text only. Throws with a user-facing message when Gemini can't do it.</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string mimeType, CancellationToken cancel) {
        var driver = await ReadyDriverAsync(cancel);
        _hasConversation = false;

        string path = Path.Combine(Path.GetTempPath(), "echotype-voice-" + Guid.NewGuid().ToString("N") + Extension(mimeType));
        await File.WriteAllBytesAsync(path, audio, cancel);
        try {
            driver.InvalidateWaits();
            string failure = Loc.T("Couldn't upload the voice note to Gemini.");
            // Gemini sometimes loses a try: the upload lands on a page that's still loading,
            // or the prompt shows in the chat but the answer never starts (the URL stays at
            // /app). Each retry starts over on a freshly reloaded page.
            for (int attempt = 1; attempt <= MaxAttempts; attempt++) {
                cancel.ThrowIfCancellationRequested();
                if (attempt > 1) {
                    await Task.Delay(1000, cancel);
                }
                try {
                    bool fresh = attempt == 1 ? await driver.StartNewChatAsync() : await driver.ReloadChatHomeAsync();
                    if (!fresh) {
                        failure = Loc.T("Couldn't open a new Gemini chat.");
                        Log.Write("telegram: no fresh Gemini chat (attempt " + attempt + ")");
                        continue;
                    }
                    CommandTurnSnapshot previous = await driver.SnapshotCommandTurnAsync();
                    await driver.SetComposerAsync(TranscribePrompt);
                    if (!await driver.AttachFilesAsync([path])) {
                        await driver.ClearComposerAsync();
                        failure = Loc.T("Couldn't upload the voice note to Gemini.");
                        Log.Write("telegram: voice note upload failed (attempt " + attempt + ")");
                        continue;
                    }
                    cancel.ThrowIfCancellationRequested();
                    string urlBefore = driver.CurrentUrl;
                    await driver.SendPromptAsync();
                    if (!await AwaitReplyStartAsync(driver, previous, urlBefore, cancel)) {
                        failure = Loc.T("Gemini didn't answer in time.");
                        Log.Write("telegram: Gemini never started answering (attempt " + attempt + "), url " + driver.CurrentUrl);
                        continue;
                    }
                    ModelReply reply = await AwaitReplyAsync(driver, previous, ReplyTimeout);
                    await driver.ClearComposerAsync();
                    _hasConversation = true;
                    return CleanTranscript(reply.Text);
                } catch (Exception ex) when ((ex is DriverException or InvalidOperationException) && !cancel.IsCancellationRequested) {
                    failure = ex.Message;
                    Log.Write("telegram: transcription attempt " + attempt + " failed: " + ex.Message);
                }
            }
            throw new InvalidOperationException(failure);
        } finally {
            try {
                File.Delete(path);
            } catch (IOException) {
                // still held by the upload; temp folder cleanup gets it later
            }
        }
    }

    /// <summary>
    /// Sends a text prompt ("organize the text", "translate it"…) to the same hidden Gemini
    /// chat that transcribed the last voice note and returns the model's answer. With no
    /// voice note yet, it goes to a new chat.
    /// </summary>
    public async Task<string> AskAsync(string prompt, CancellationToken cancel) {
        var driver = await ReadyDriverAsync(cancel);
        driver.InvalidateWaits();
        if (_hasConversation) {
            await driver.StopGenerationAsync();
        } else if (!await driver.StartNewChatAsync()) {
            throw new InvalidOperationException(Loc.T("Couldn't open a new Gemini chat."));
        }
        CommandTurnSnapshot previous = await driver.SnapshotCommandTurnAsync();
        await driver.SetComposerAsync(prompt);
        cancel.ThrowIfCancellationRequested();
        await driver.SendPromptAsync();
        ModelReply reply = await AwaitReplyAsync(driver, previous, FollowUpReplyTimeout);
        await driver.ClearComposerAsync();
        _hasConversation = true;
        return reply.Text.Trim();
    }

    private async Task<DictationDriver> ReadyDriverAsync(CancellationToken cancel) {
        // Same profile folder as the Gemini model, so the existing login is reused.
        if (_web == null) {
            _web = new ChatGPTWebController(ChatSite.Gemini);
            _hasConversation = false;
        }
        var ready = await _web.EnsureReadyAsync();
        cancel.ThrowIfCancellationRequested();
        if (ready == ReadyOutcome.LoggedOut) {
            throw new InvalidOperationException(
                Loc.T("Gemini is signed out. Pick Gemini in EchoType, log in, then send the voice note again."));
        }
        if (ready != ReadyOutcome.Ready) {
            throw new InvalidOperationException(Loc.F("Gemini isn't ready ({0}).", ready));
        }
        return _web.Driver ?? throw new InvalidOperationException(Loc.F("Gemini isn't ready ({0}).", "no driver"));
    }

    /// <summary>True once Gemini shows it got the prompt: a real chat URL, generating, or reply text.</summary>
    private static async Task<bool> AwaitReplyStartAsync(
        DictationDriver driver, CommandTurnSnapshot previous, string urlBefore, CancellationToken cancel) {
        DateTime deadline = DateTime.UtcNow + ReplyStartTimeout;
        while (DateTime.UtcNow < deadline) {
            cancel.ThrowIfCancellationRequested();
            string url = driver.CurrentUrl;
            if (url != urlBefore && url.Contains("/app/", StringComparison.Ordinal)) {
                Log.Write("telegram: Gemini opened the chat " + url);
                return true;
            }
            if (await driver.ReplyStartedAsync(previous)) {
                Log.Write("telegram: Gemini is answering");
                return true;
            }
            await Task.Delay(500, cancel);
        }
        return false;
    }

    private static async Task<ModelReply> AwaitReplyAsync(DictationDriver driver, CommandTurnSnapshot previous, TimeSpan timeout) {
        ModelReply reply;
        try {
            reply = await driver.AwaitAssistantReplyAsync(previous, timeout);
        } catch (DriverException ex) when (ex.Kind == DriverFailure.Timeout) {
            reply = await driver.TryRecoverAssistantReplyAsync(previous);
            if (reply.IsEmpty) {
                throw new InvalidOperationException(Loc.T("Gemini didn't answer in time."));
            }
        }
        if (reply.IsEmpty) {
            reply = await driver.TryRecoverAssistantReplyAsync(previous);
        }
        return reply;
    }

    /// <summary>Gemini's file picker goes by extension, so the temp file needs the right one.</summary>
    private static string Extension(string mimeType) => mimeType.ToLowerInvariant() switch {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/wav" or "audio/x-wav" => ".wav",
        "video/mp4" => ".mp4",
        _ => ".ogg",
    };

    /// <summary>Drops wrappers a model may still add ("Transcript:", quotes, code fences).</summary>
    private static string CleanTranscript(string text) {
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal)) {
            text = text.Trim('`').Trim();
        }
        foreach (string label in new[] { "Transcript:", "Transcription:", "النص:" }) {
            if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase)) {
                text = text[label.Length..].Trim();
            }
        }
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '«' && text[^1] == '»'))) {
            text = text[1..^1].Trim();
        }
        return text;
    }
}
