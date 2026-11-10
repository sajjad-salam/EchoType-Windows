using System.Text.Json;
using EchoType.Update;

namespace EchoType.Web;

/// <summary>
/// Downloads the community selector fixes published in the repository
/// (<c>selectors/selectors.json</c>, same format as the local selectors.json) to
/// <see cref="SelectorOverrides.RemoteFilePath"/>. When ChatGPT, Gemini or Claude change their
/// page, one fix in the repo reaches every EchoType without a new build.
/// </summary>
internal static class RemoteSelectors {

    private const string Url = "https://raw.githubusercontent.com/" + Updater.Repository + "/main/selectors/selectors.json";

    /// <summary>Fetches the file; true when it changed (pages should reload to pick it up).</summary>
    public static async Task<bool> RefreshAsync(CancellationToken cancel = default) {
        try {
            using var http = Updater.CreateClient();
            string text = await http.GetStringAsync(Url, cancel);
            using (var doc = JsonDocument.Parse(text, new JsonDocumentOptions {
                       CommentHandling = JsonCommentHandling.Skip,
                       AllowTrailingCommas = true,
                   })) {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) {
                    Log.Write("selectors: remote file is not a JSON object, ignored");
                    return false;
                }
            }
            string path = SelectorOverrides.RemoteFilePath;
            if (File.Exists(path) && File.ReadAllText(path) == text) {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            Log.Write("selectors: community fixes updated (" + text.Length + " chars)");
            return true;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Log.Write("selectors: community fixes not fetched: " + ex.Message);
            return false;
        }
    }

    /// <summary>Removes the downloaded fixes (when the option is turned off).</summary>
    public static void Delete() {
        try {
            File.Delete(SelectorOverrides.RemoteFilePath);
        } catch (Exception ex) {
            Log.Write("selectors: could not delete community fixes: " + ex.Message);
        }
    }
}
