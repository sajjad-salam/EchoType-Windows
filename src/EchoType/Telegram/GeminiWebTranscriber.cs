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

    private static readonly TimeSpan FirstReplyTimeout = TimeSpan.FromSeconds(60);

    private const string TranscribePrompt =
        "Transcribe the attached audio exactly as spoken. Output ONLY the spoken words: no introduction, no comments, "
        + "no timestamps, no speaker labels, no quotes, no translation and no summary. Keep the original language "
        + "and dialect (for example Iraqi Arabic), and write English words and names in Latin letters. "
        + "If nothing intelligible is said, output nothing.";

    private ChatGPTWebController? _web;

    public void Dispose() {
        _web?.Dispose();
        _web = null;
    }

    /// <summary>Speech → text only. Throws with a user-facing message when Gemini can't do it.</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string mimeType, CancellationToken cancel) {
        // Same profile folder as the Gemini model, so the existing login is reused.
        _web ??= new ChatGPTWebController(ChatSite.Gemini);
        var ready = await _web.EnsureReadyAsync();
        cancel.ThrowIfCancellationRequested();
        if (ready == ReadyOutcome.LoggedOut) {
            throw new InvalidOperationException(
                Loc.T("Gemini is signed out. Pick Gemini in EchoType, log in, then send the voice note again."));
        }
        if (ready != ReadyOutcome.Ready) {
            throw new InvalidOperationException(Loc.F("Gemini isn't ready ({0}).", ready));
        }
        var driver = _web.Driver ?? throw new InvalidOperationException(Loc.F("Gemini isn't ready ({0}).", "no driver"));

        string path = Path.Combine(Path.GetTempPath(), "echotype-voice-" + Guid.NewGuid().ToString("N") + Extension(mimeType));
        await File.WriteAllBytesAsync(path, audio, cancel);
        try {
            driver.InvalidateWaits();
            if (!await driver.StartNewChatAsync()) {
                throw new InvalidOperationException(Loc.T("Couldn't open a new Gemini chat."));
            }
            CommandTurnSnapshot previous = await driver.SnapshotCommandTurnAsync();
            await driver.SetComposerAsync(TranscribePrompt);
            if (!await driver.AttachFilesAsync([path])) {
                await driver.ClearComposerAsync();
                throw new InvalidOperationException(Loc.T("Gemini didn't accept the voice note file."));
            }
            cancel.ThrowIfCancellationRequested();
            await driver.SendPromptAsync();
            ModelReply reply;
            try {
                reply = await driver.AwaitAssistantReplyAsync(previous, FirstReplyTimeout);
            } catch (DriverException ex) when (ex.Kind == DriverFailure.Timeout) {
                reply = await driver.TryRecoverAssistantReplyAsync(previous);
                if (reply.IsEmpty) {
                    throw new InvalidOperationException(Loc.T("Gemini didn't answer in time."));
                }
            }
            if (reply.IsEmpty) {
                reply = await driver.TryRecoverAssistantReplyAsync(previous);
            }
            await driver.ClearComposerAsync();
            return CleanTranscript(reply.Text);
        } finally {
            try {
                File.Delete(path);
            } catch (IOException) {
                // still held by the upload; temp folder cleanup gets it later
            }
        }
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
