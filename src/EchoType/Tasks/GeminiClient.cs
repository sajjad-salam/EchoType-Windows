using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EchoType.Tasks;

/// <summary>
/// Google Gemini API with the user's own key: transcribes Telegram voice notes (the audio
/// is uploaded to Gemini) and reads ideas/tasks out of free text as JSON.
/// </summary>
internal sealed class GeminiClient {

    public const string DefaultModel = "gemini-flash-latest";

    private const string Base = "https://generativelanguage.googleapis.com";
    /// <summary>Requests bigger than this go through the Files API instead of inline data.</summary>
    private const int InlineLimitBytes = 14 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly JsonSerializerOptions RelaxedJson = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string TranscribePrompt =
        "Transcribe this audio exactly as spoken. Output ONLY the spoken words: no introduction, no comments, "
        + "no timestamps, no speaker labels, no quotes, no translation and no summary. Keep the original language "
        + "and dialect (for example Iraqi Arabic), and write English words and names in Latin letters. "
        + "If nothing intelligible is said, output nothing.";

    private readonly string _apiKey;
    private readonly string _model;

    public GeminiClient(string apiKey, string? model) {
        _apiKey = apiKey.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    public string Model => _model;

    /// <summary>The key from settings, or the GEMINI_API_KEY environment variable. Empty = not configured.</summary>
    public static string ResolveKey(Settings settings) {
        string key = settings.GeminiApiKey?.Trim() ?? "";
        if (key.Length > 0) {
            return key;
        }
        return Environment.GetEnvironmentVariable("GEMINI_API_KEY")?.Trim() ?? "";
    }

    public static GeminiClient? FromSettings(Settings settings) {
        string key = ResolveKey(settings);
        return key.Length == 0 ? null : new GeminiClient(key, settings.GeminiModel);
    }

    /// <summary>Speech → text only. Throws on API errors.</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string mimeType, CancellationToken cancel) {
        JsonObject media;
        if (audio.Length > InlineLimitBytes) {
            string uri = await UploadAsync(audio, mimeType, cancel);
            media = new JsonObject {
                ["file_data"] = new JsonObject { ["mime_type"] = mimeType, ["file_uri"] = uri },
            };
        } else {
            media = new JsonObject {
                ["inline_data"] = new JsonObject {
                    ["mime_type"] = mimeType,
                    ["data"] = Convert.ToBase64String(audio),
                },
            };
        }
        var parts = new JsonArray { new JsonObject { ["text"] = TranscribePrompt }, media };
        string text = await GenerateAsync(parts, system: null, json: false, cancel);
        return CleanTranscript(text);
    }

    /// <summary>A prompt that must be answered with one JSON object.</summary>
    public Task<string> GenerateJsonAsync(string system, string user, CancellationToken cancel) =>
        GenerateAsync(new JsonArray { new JsonObject { ["text"] = user } }, system, json: true, cancel);

    /// <summary>Checks the key and model with a tiny request; returns null when it works, else the error.</summary>
    public async Task<string?> TestAsync(CancellationToken cancel) {
        try {
            string reply = await GenerateAsync(new JsonArray { new JsonObject { ["text"] = "Reply with the word OK." } },
                system: null, json: false, cancel);
            return reply.Length > 0 ? null : "Empty reply.";
        } catch (Exception ex) {
            return ex.Message;
        }
    }

    private async Task<string> GenerateAsync(JsonArray parts, string? system, bool json, CancellationToken cancel) {
        var body = new JsonObject {
            ["contents"] = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = parts } },
            ["generationConfig"] = new JsonObject {
                ["temperature"] = json ? 0.2 : 0.0,
            },
        };
        if (json) {
            body["generationConfig"]!["responseMimeType"] = "application/json";
        }
        if (system != null) {
            body["systemInstruction"] = new JsonObject {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = system } },
            };
        }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Base + "/v1beta/models/" + Uri.EscapeDataString(_model) + ":generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = new StringContent(body.ToJsonString(RelaxedJson), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancel);
        string raw = await response.Content.ReadAsStringAsync(cancel);
        if (!response.IsSuccessStatusCode) {
            throw new InvalidOperationException("Gemini " + (int)response.StatusCode + ": " + ErrorMessage(raw));
        }
        var root = JsonNode.Parse(raw);
        var candidate = root?["candidates"]?[0];
        if (candidate == null) {
            string? blocked = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new InvalidOperationException("Gemini returned no answer" + (blocked != null ? " (" + blocked + ")" : "") + ".");
        }
        var sb = new StringBuilder();
        if (candidate["content"]?["parts"] is JsonArray replyParts) {
            foreach (var part in replyParts) {
                // Thinking models may add thought parts; only the answer text counts.
                if (part?["thought"]?.GetValue<bool>() == true) {
                    continue;
                }
                if (part?["text"]?.GetValue<string>() is { } t) {
                    sb.Append(t);
                }
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>Files API (resumable upload) for audio too big to send inline.</summary>
    private async Task<string> UploadAsync(byte[] data, string mimeType, CancellationToken cancel) {
        using var start = new HttpRequestMessage(HttpMethod.Post, Base + "/upload/v1beta/files");
        start.Headers.Add("x-goog-api-key", _apiKey);
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", data.Length.ToString());
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        start.Content = new StringContent("{\"file\":{\"display_name\":\"echotype-voice\"}}", Encoding.UTF8, "application/json");
        using var started = await Http.SendAsync(start, cancel);
        if (!started.IsSuccessStatusCode
            || !started.Headers.TryGetValues("X-Goog-Upload-URL", out var urls)
            || urls.FirstOrDefault() is not { Length: > 0 } uploadUrl) {
            throw new InvalidOperationException("Gemini upload " + (int)started.StatusCode + ": "
                + ErrorMessage(await started.Content.ReadAsStringAsync(cancel)));
        }

        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        upload.Headers.Add("X-Goog-Upload-Offset", "0");
        upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        upload.Content = new ByteArrayContent(data);
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        using var uploaded = await Http.SendAsync(upload, cancel);
        string raw = await uploaded.Content.ReadAsStringAsync(cancel);
        if (!uploaded.IsSuccessStatusCode) {
            throw new InvalidOperationException("Gemini upload " + (int)uploaded.StatusCode + ": " + ErrorMessage(raw));
        }
        var file = JsonNode.Parse(raw)?["file"];
        string uri = file?["uri"]?.GetValue<string>() ?? throw new InvalidOperationException("Gemini upload: no file uri.");
        string? name = file?["name"]?.GetValue<string>();
        string state = file?["state"]?.GetValue<string>() ?? "ACTIVE";
        // Audio is usually ready at once; wait briefly while it is still processing.
        for (int i = 0; i < 30 && state == "PROCESSING" && name != null; i++) {
            await Task.Delay(1000, cancel);
            using var get = new HttpRequestMessage(HttpMethod.Get, Base + "/v1beta/" + name);
            get.Headers.Add("x-goog-api-key", _apiKey);
            using var polled = await Http.SendAsync(get, cancel);
            state = JsonNode.Parse(await polled.Content.ReadAsStringAsync(cancel))?["state"]?.GetValue<string>() ?? "ACTIVE";
        }
        return uri;
    }

    private static string ErrorMessage(string raw) {
        try {
            if (JsonNode.Parse(raw)?["error"]?["message"]?.GetValue<string>() is { Length: > 0 } message) {
                return message;
            }
        } catch (JsonException) {
            // not JSON
        }
        return raw.Length > 200 ? raw[..200] : raw;
    }

    /// <summary>Drops wrappers a model may still add ("Transcript:", quotes, code fences).</summary>
    internal static string CleanTranscript(string text) {
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
