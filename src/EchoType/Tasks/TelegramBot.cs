using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EchoType.Tasks;

/// <summary>A message from the user's own chat.</summary>
internal sealed class TelegramIncoming {
    public long MessageId { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Voice note or audio file to transcribe; null for text.</summary>
    public string? AudioFileId { get; init; }
    public string AudioMime { get; init; } = "audio/ogg";
    public long AudioSize { get; init; }
}

/// <summary>
/// Minimal Telegram Bot API client with long polling (no webhook, no server): EchoType asks
/// Telegram for new messages while it runs, and Telegram keeps whatever arrives meanwhile
/// for 24 hours. Only the configured chat is served; anyone else gets their chat id and nothing more.
/// </summary>
internal sealed class TelegramBot : IDisposable {

    private const string Api = "https://api.telegram.org";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static readonly JsonSerializerOptions RelaxedJson = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _token;
    private readonly long _chatId;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Raised on a background thread for each message from the user's chat.</summary>
    public event Func<TelegramIncoming, Task>? MessageReceived;

    /// <summary>Raised on a background thread when the user votes in a reminder poll: (poll id, option index).</summary>
    public event Action<string, int>? PollAnswered;

    public TelegramBot(string token, long chatId) {
        _token = token.Trim();
        _chatId = chatId;
    }

    public long ChatId => _chatId;

    public static bool TryParseChatId(string? text, out long chatId) =>
        long.TryParse((text ?? "").Trim(), out chatId) && chatId != 0;

    public void Start(Func<long> readOffset, Action<long> saveOffset) {
        _loop ??= Task.Run(() => PollLoopAsync(readOffset, saveOffset, _stop.Token));
    }

    public void Dispose() {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task PollLoopAsync(Func<long> readOffset, Action<long> saveOffset, CancellationToken cancel) {
        int failures = 0;
        bool webhookCleared = false;
        try {
            await CallAsync("setMyCommands", new JsonObject {
                ["commands"] = new JsonArray {
                    Command("tasks", "Today, overdue and upcoming tasks"),
                    Command("inbox", "Ideas and tasks in the Inbox"),
                    Command("projects", "Projects and their items"),
                    Command("undo", "Undo the last change"),
                    Command("help", "What this bot does"),
                },
            }, cancel);
        } catch (Exception ex) when (!cancel.IsCancellationRequested) {
            Log.Write("telegram: setMyCommands failed: " + ex.Message);
        }
        Log.Write("telegram: polling started");
        while (!cancel.IsCancellationRequested) {
            try {
                var updates = await CallAsync("getUpdates", new JsonObject {
                    ["offset"] = readOffset(),
                    ["timeout"] = 50,
                    ["allowed_updates"] = new JsonArray { "message", "poll_answer" },
                }, cancel, TimeSpan.FromSeconds(75));
                failures = 0;
                if (updates is not JsonArray list) {
                    continue;
                }
                foreach (var update in list) {
                    long id = update?["update_id"]?.GetValue<long>() ?? 0;
                    // Saved before handling: a message that crashes the handler is not retried forever.
                    saveOffset(id + 1);
                    try {
                        await HandleUpdateAsync(update as JsonObject, cancel);
                    } catch (Exception ex) when (!cancel.IsCancellationRequested) {
                        Log.Write("telegram: update " + id + " failed: " + ex.Message);
                    }
                }
            } catch (OperationCanceledException) when (cancel.IsCancellationRequested) {
                break;
            } catch (TelegramException ex) when (ex.Code == 409 && !webhookCleared) {
                // A webhook set elsewhere blocks getUpdates.
                webhookCleared = true;
                Log.Write("telegram: 409 (" + ex.Message + "), removing webhook");
                try {
                    await CallAsync("deleteWebhook", new JsonObject(), cancel);
                } catch (Exception inner) {
                    Log.Write("telegram: deleteWebhook failed: " + inner.Message);
                }
            } catch (TelegramException ex) when (ex.Code == 401) {
                Log.Write("telegram: token rejected (401), polling stopped");
                return;
            } catch (Exception ex) {
                failures++;
                int wait = Math.Min(60, 3 * failures);
                Log.Write("telegram: getUpdates failed (" + ex.Message + "), retry in " + wait + "s");
                try {
                    await Task.Delay(TimeSpan.FromSeconds(wait), cancel);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        }
        Log.Write("telegram: polling stopped");
    }

    private static JsonObject Command(string name, string description) =>
        new() { ["command"] = name, ["description"] = description };

    private async Task HandleUpdateAsync(JsonObject? update, CancellationToken cancel) {
        if (update == null) {
            return;
        }
        if (update["poll_answer"] is JsonObject answer) {
            long user = answer["user"]?["id"]?.GetValue<long>() ?? 0;
            string? pollId = answer["poll_id"]?.GetValue<string>();
            if (user != _chatId || pollId == null) {
                return;
            }
            if (answer["option_ids"] is JsonArray { Count: > 0 } options) {
                PollAnswered?.Invoke(pollId, options[0]!.GetValue<int>());
            }
            return;
        }
        if (update["message"] is not JsonObject message) {
            return;
        }
        long chat = message["chat"]?["id"]?.GetValue<long>() ?? 0;
        if (chat != _chatId) {
            Log.Write("telegram: ignored a message from chat " + chat);
            if (chat != 0) {
                await SendMessageAsync(chat, "This EchoType bot is private. Your chat id is " + chat
                    + " — paste it into EchoType → Tasks & sync if this is your bot.", null, cancel);
            }
            return;
        }
        string text = message["text"]?.GetValue<string>() ?? message["caption"]?.GetValue<string>() ?? "";
        JsonObject? audio = message["voice"] as JsonObject ?? message["audio"] as JsonObject
            ?? message["video_note"] as JsonObject;
        if (audio == null && message["document"] is JsonObject doc
            && (doc["mime_type"]?.GetValue<string>() ?? "").StartsWith("audio/", StringComparison.Ordinal)) {
            audio = doc;
        }
        var incoming = new TelegramIncoming {
            MessageId = message["message_id"]?.GetValue<long>() ?? 0,
            Text = text,
            AudioFileId = audio?["file_id"]?.GetValue<string>(),
            AudioMime = audio?["mime_type"]?.GetValue<string>()
                ?? (message["video_note"] != null ? "video/mp4" : "audio/ogg"),
            AudioSize = audio?["file_size"]?.GetValue<long>() ?? 0,
        };
        if (incoming.Text.Length == 0 && incoming.AudioFileId == null) {
            return;
        }
        if (MessageReceived is { } handler) {
            await handler(incoming);
        }
    }

    // ── Sending ───────────────────────────────────────────────

    public Task SendMessageAsync(string text, long? replyTo = null, CancellationToken cancel = default) =>
        SendMessageAsync(_chatId, text, replyTo, cancel);

    private async Task SendMessageAsync(long chat, string text, long? replyTo, CancellationToken cancel) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }
        // Telegram caps a message at 4096 characters.
        foreach (string part in Split(text, 4000)) {
            var body = new JsonObject {
                ["chat_id"] = chat,
                ["text"] = part,
                ["disable_web_page_preview"] = true,
            };
            if (replyTo is { } r && r > 0) {
                body["reply_parameters"] = new JsonObject { ["message_id"] = r, ["allow_sending_without_reply"] = true };
                replyTo = null;
            }
            await CallAsync("sendMessage", body, cancel);
        }
    }

    public async Task TypingAsync(CancellationToken cancel = default) {
        try {
            await CallAsync("sendChatAction", new JsonObject { ["chat_id"] = _chatId, ["action"] = "typing" }, cancel);
        } catch (Exception ex) {
            Log.Write("telegram: typing failed: " + ex.Message);
        }
    }

    /// <summary>Sends a non-anonymous poll (so EchoType sees the vote). Returns (message id, poll id).</summary>
    public async Task<(long MessageId, string PollId)> SendPollAsync(string question, IReadOnlyList<string> options,
        CancellationToken cancel = default) {
        var optionArray = new JsonArray();
        foreach (var option in options) {
            optionArray.Add(new JsonObject { ["text"] = Clip(option, 100) });
        }
        var result = await CallAsync("sendPoll", new JsonObject {
            ["chat_id"] = _chatId,
            ["question"] = Clip(question, 300),
            ["options"] = optionArray,
            ["is_anonymous"] = false,
            ["allows_multiple_answers"] = false,
        }, cancel);
        long messageId = result?["message_id"]?.GetValue<long>() ?? 0;
        string pollId = result?["poll"]?["id"]?.GetValue<string>() ?? "";
        return (messageId, pollId);
    }

    public async Task StopPollAsync(long messageId, CancellationToken cancel = default) {
        try {
            await CallAsync("stopPoll", new JsonObject { ["chat_id"] = _chatId, ["message_id"] = messageId }, cancel);
        } catch (TelegramException ex) {
            // Already closed, or too old to edit — nothing to do.
            Log.Write("telegram: stopPoll " + messageId + ": " + ex.Message);
        }
    }

    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancel = default) {
        var file = await CallAsync("getFile", new JsonObject { ["file_id"] = fileId }, cancel);
        string path = file?["file_path"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Telegram returned no file path (files over 20 MB can't be downloaded by bots).");
        return await Http.GetByteArrayAsync(Api + "/file/bot" + _token + "/" + path, cancel);
    }

    /// <summary>Checks a token; returns the bot's @username, or throws with Telegram's reason.</summary>
    public static async Task<string> GetUsernameAsync(string token, CancellationToken cancel = default) {
        using var bot = new TelegramBot(token, 0);
        var me = await bot.CallAsync("getMe", new JsonObject(), cancel);
        return me?["username"]?.GetValue<string>() ?? "?";
    }

    private async Task<JsonNode?> CallAsync(string method, JsonObject body, CancellationToken cancel, TimeSpan? timeout = null) {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        using var content = new StringContent(body.ToJsonString(RelaxedJson), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try {
            response = await Http.PostAsync(Api + "/bot" + _token + "/" + method, content, timeoutCts.Token);
        } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) {
            throw new TimeoutException("Telegram " + method + " timed out.");
        }
        using (response) {
            string raw = await response.Content.ReadAsStringAsync(cancel);
            JsonNode? root = null;
            try {
                root = JsonNode.Parse(raw);
            } catch (JsonException) {
                // reported below
            }
            if (root?["ok"]?.GetValue<bool>() != true) {
                string description = root?["description"]?.GetValue<string>() ?? ("HTTP " + (int)response.StatusCode);
                int code = root?["error_code"]?.GetValue<int>() ?? (int)response.StatusCode;
                throw new TelegramException(code, description);
            }
            return root["result"];
        }
    }

    private static IEnumerable<string> Split(string text, int max) {
        for (int i = 0; i < text.Length; i += max) {
            yield return text.Substring(i, Math.Min(max, text.Length - i));
        }
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

internal sealed class TelegramException(int code, string message) : Exception(message) {
    public int Code { get; } = code;
}
