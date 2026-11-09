using System.Text;

namespace EchoType.Telegram;

/// <summary>
/// Runs the exact Telegram voice-note path (Gemini web, hidden window) on a local audio
/// file and writes the outcome to a text file, so the flow can be tested end to end
/// without sending anything through Telegram. Optionally sends a follow-up prompt to
/// the same chat afterwards.
/// </summary>
internal static class TranscribeTest {

    public static void Run(string audioPath, string resultPath, string? followUp) {
        using var ui = new Form { ShowInTaskbar = false, Opacity = 0, Width = 1, Height = 1 };
        var gemini = new GeminiWebTranscriber();
        var report = new StringBuilder();
        ui.Shown += async (_, _) => {
            var started = DateTime.UtcNow;
            try {
                if (audioPath == "-") {
                    // Text only: is Gemini answering at all in this profile?
                    using var t = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                    string answer = await gemini.AskAsync(followUp ?? "Say OK.", t.Token);
                    report.AppendLine("TEXT OK in " + (int)(DateTime.UtcNow - started).TotalSeconds + "s:");
                    report.AppendLine(answer);
                    throw new OperationCanceledException("done");
                }
                byte[] audio = await File.ReadAllBytesAsync(audioPath);
                string mime = Path.GetExtension(audioPath).ToLowerInvariant() switch {
                    ".wav" => "audio/wav",
                    ".mp3" => "audio/mpeg",
                    ".m4a" => "audio/mp4",
                    _ => "audio/ogg",
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                string text;
                try {
                    text = await gemini.TranscribeAsync(audio, mime, timeout.Token);
                } catch (GeminiRobotCheckException) {
                    // The window is shown; wait for a person to solve the check, then go again.
                    Log.Write("transcribe-test: waiting for the robot check to be solved");
                    while (gemini.IsBlockedByRobotCheck) {
                        await Task.Delay(1000, timeout.Token);
                    }
                    Log.Write("transcribe-test: robot check solved, retrying");
                    await Task.Delay(3000, timeout.Token);
                    started = DateTime.UtcNow;
                    text = await gemini.TranscribeAsync(audio, mime, timeout.Token);
                }
                report.AppendLine("TRANSCRIPT OK in " + (int)(DateTime.UtcNow - started).TotalSeconds + "s:");
                report.AppendLine(text);
                if (!string.IsNullOrWhiteSpace(followUp)) {
                    started = DateTime.UtcNow;
                    string answer = await gemini.AskAsync(followUp, timeout.Token);
                    report.AppendLine("FOLLOW-UP OK in " + (int)(DateTime.UtcNow - started).TotalSeconds + "s:");
                    report.AppendLine(answer);
                }
            } catch (OperationCanceledException ex) when (ex.Message == "done") {
                // text-only run finished
            } catch (Exception ex) {
                report.AppendLine("FAILED after " + (int)(DateTime.UtcNow - started).TotalSeconds + "s: " + ex.Message);
            }
            Log.Write("transcribe-test: " + report.ToString().Replace("\n", " | "));
            await File.WriteAllTextAsync(resultPath, report.ToString(), Encoding.UTF8);
            gemini.Dispose();
            ui.Close();
        };
        Application.Run(ui);
    }
}
