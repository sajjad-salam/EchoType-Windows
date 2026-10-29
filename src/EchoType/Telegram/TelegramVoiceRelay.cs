using EchoType.Ui;

namespace EchoType.Telegram;

/// <summary>
/// While EchoType runs, voice notes sent to the user's Telegram bot from their chat are
/// downloaded, transcribed by Gemini web (hidden window, no API key) and the text is sent
/// back to that chat as a reply.
/// Other chats only get their chat id (to help with setup).
/// </summary>
internal sealed class TelegramVoiceRelay : IDisposable {

    private static readonly string OffsetPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoType", "telegram-offset.txt");

    private readonly TelegramBot _bot;
    private readonly long _chatId;
    /// <summary>UI-thread control: the Gemini window (WebView2) is only driven from there.</summary>
    private readonly Control _ui;
    private readonly GeminiWebTranscriber _gemini = new();

    private TelegramVoiceRelay(string token, long chatId, Control ui) {
        _bot = new TelegramBot(token);
        _chatId = chatId;
        _ui = ui;
        _bot.MessageReceived += OnMessageAsync;
    }

    /// <summary>Starts polling when Telegram is on and has a token; null otherwise.</summary>
    public static TelegramVoiceRelay? Start(Settings settings, Control ui) {
        if (!settings.TelegramEnabled || string.IsNullOrWhiteSpace(settings.TelegramBotToken)) {
            return null;
        }
        TelegramBot.TryParseChatId(settings.TelegramChatId, out long chatId);
        var relay = new TelegramVoiceRelay(settings.TelegramBotToken, chatId, ui);
        relay._bot.Start(ReadOffset, SaveOffset);
        Log.Write("telegram: voice relay on" + (chatId == 0 ? " (no chat id yet)" : ""));
        return relay;
    }

    /// <summary>Call on the UI thread (closes the hidden Gemini window).</summary>
    public void Dispose() {
        _bot.Dispose();
        _gemini.Dispose();
    }

    private async Task OnMessageAsync(TelegramIncoming message, CancellationToken cancel) {
        if (_chatId == 0 || message.ChatId != _chatId) {
            Log.Write("telegram: message from chat " + message.ChatId + " (not the configured chat)");
            await _bot.SendMessageAsync(message.ChatId,
                Loc.F("Your chat id is {0}. Paste it into EchoType → Settings → Telegram voice notes to use this bot.", message.ChatId),
                message.MessageId, cancel);
            return;
        }
        if (message.AudioFileId == null) {
            await _bot.SendMessageAsync(_chatId,
                Loc.T("Send me a voice note and I'll reply with its text."), message.MessageId, cancel);
            return;
        }
        var started = DateTime.UtcNow;
        Log.Write("telegram: voice note " + message.MessageId + " (" + message.AudioMime + ", " + message.AudioSize + " bytes)");
        // "typing…" lasts about 5 s; keep it up while Gemini works.
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var typingLoop = KeepTypingAsync(typing.Token);
        string reply;
        try {
            byte[] audio = await _bot.DownloadFileAsync(message.AudioFileId, cancel);
            string text = await OnUiAsync(() => _gemini.TranscribeAsync(audio, message.AudioMime, cancel));
            reply = text.Length > 0 ? text : Loc.T("(No speech found in this voice note.)");
            Log.Write("telegram: voice note " + message.MessageId + " transcribed, " + text.Length + " chars in "
                + (int)(DateTime.UtcNow - started).TotalMilliseconds + " ms");
        } catch (Exception ex) when (!cancel.IsCancellationRequested) {
            Log.Write("telegram: voice note " + message.MessageId + " failed: " + ex.Message);
            reply = Loc.T("Couldn't transcribe this voice note: ") + ex.Message;
        } finally {
            typing.Cancel();
        }
        await typingLoop;
        await _bot.SendMessageAsync(_chatId, reply, message.MessageId, cancel);
    }

    private Task<string> OnUiAsync(Func<Task<string>> work) {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.BeginInvoke(new Action(async () => {
            try {
                done.SetResult(await work());
            } catch (Exception ex) {
                done.SetException(ex);
            }
        }));
        return done.Task;
    }

    private async Task KeepTypingAsync(CancellationToken cancel) {
        try {
            while (!cancel.IsCancellationRequested) {
                await _bot.TypingAsync(_chatId, cancel);
                await Task.Delay(4500, cancel);
            }
        } catch (OperationCanceledException) {
            // transcription finished
        }
    }

    private static long ReadOffset() {
        try {
            return File.Exists(OffsetPath) && long.TryParse(File.ReadAllText(OffsetPath).Trim(), out long offset) ? offset : 0;
        } catch (Exception ex) {
            Log.Write("telegram: read offset failed: " + ex.Message);
            return 0;
        }
    }

    private static void SaveOffset(long offset) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(OffsetPath)!);
            File.WriteAllText(OffsetPath, offset.ToString());
        } catch (Exception ex) {
            Log.Write("telegram: save offset failed: " + ex.Message);
        }
    }
}
