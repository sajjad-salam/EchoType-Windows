using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EchoType.Telegram;

/// <summary>A message sent to the bot.</summary>
internal sealed class TelegramIncoming {
    public long ChatId { get; init; }
    public long MessageId { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Voice note, audio file or video note; null for a text message.</summary>
    public string? AudioFileId { get; init; }
    public string AudioMime { get; init; } = "audio/ogg";
    public long AudioSize { get; init; }
}

/// <summary>
/// Minimal Telegram Bot API client with long polling (no webhook, no server): EchoType asks
/// Telegram for new messages while it runs, and Telegram keeps whatever arrives meanwhile
/// for 24 hours.
/// </summary>
internal sealed class TelegramBot : IDisposable {

    private const string Api = "https://api.telegram.org";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static readonly JsonSerializerOptions RelaxedJson = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _token;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Raised on a background thread for each message, from any chat.</summary>
    public event Func<TelegramIncoming, CancellationToken, Task>? MessageReceived;

    public TelegramBot(string token) {
        _token = token.Trim();
    }

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
        Log.Write("telegram: polling started");
        while (!cancel.IsCancellationRequested) {
            try {
                var updates = await CallAsync("getUpdates", new JsonObject {
                    ["offset"] = readOffset(),
                    ["timeout"] = 50,
                    ["allowed_updates"] = new JsonArray { "message" },
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

    private async Task HandleUpdateAsync(JsonObject? update, CancellationToken cancel) {
        if (update?["message"] is not JsonObject message) {
            return;
        }
        long chat = message["chat"]?["id"]?.GetValue<long>() ?? 0;
        if (chat == 0) {
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
            ChatId = chat,
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
            await handler(incoming, cancel);
        }
    }

    // ── Sending ───────────────────────────────────────────────

    public async Task SendMessageAsync(long chat, string text, long? replyTo = null, CancellationToken cancel = default) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }
        // Telegram caps a message at 4096 characters.
        foreach (string part in Split(text, 4000)) {
            var body = new JsonObject {
                ["chat_id"] = chat,
                ["text"] = part,
                ["link_preview_options"] = new JsonObject { ["is_disabled"] = true },
            };
            if (replyTo is { } r && r > 0) {
                body["reply_parameters"] = new JsonObject { ["message_id"] = r, ["allow_sending_without_reply"] = true };
                replyTo = null;
            }
            await CallAsync("sendMessage", body, cancel);
        }
    }

    public async Task TypingAsync(long chat, CancellationToken cancel = default) {
        try {
            await CallAsync("sendChatAction", new JsonObject { ["chat_id"] = chat, ["action"] = "typing" }, cancel);
        } catch (Exception ex) when (!cancel.IsCancellationRequested) {
            Log.Write("telegram: typing failed: " + ex.Message);
        }
    }

    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancel = default) {
        var file = await CallAsync("getFile", new JsonObject { ["file_id"] = fileId }, cancel);
        string path = file?["file_path"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Telegram returned no file path (bots can't download files over 20 MB).");
        return await Http.GetByteArrayAsync(Api + "/file/bot" + _token + "/" + path, cancel);
    }

    /// <summary>Checks a token; returns the bot's @username, or throws with Telegram's reason.</summary>
    public static async Task<string> GetUsernameAsync(string token, CancellationToken cancel = default) {
        using var bot = new TelegramBot(token);
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
}

internal sealed class TelegramException(int code, string message) : Exception(message) {
    public int Code { get; } = code;
}
