using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoType.History;

/// <summary>One finished (or failed) dictation, reply or translation.</summary>
internal sealed class HistoryEntry {
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("time")]
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>dictation, auto-clean, command (…), selection-edit, ask-model, translate, translate-selection.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "dictation";

    /// <summary>Process name of the app the recording started in ("" when none).</summary>
    [JsonPropertyName("app")]
    public string App { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>What was said (or the selected text, for Translate selection).</summary>
    [JsonPropertyName("transcript")]
    public string Transcript { get; set; } = "";

    /// <summary>What was pasted or shown: the transcript itself, the cleaned text, the reply or the translation.</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    /// <summary>True when the step after transcription failed; <see cref="Result"/> then holds the transcript.</summary>
    [JsonPropertyName("failed")]
    public bool Failed { get; set; }

    /// <summary>Short extra detail, e.g. the failure message or "reply had an image".</summary>
    [JsonPropertyName("note")]
    public string Note { get; set; } = "";

    [JsonIgnore]
    public string Text => Result.Length > 0 ? Result : Transcript;
}

/// <summary>
/// Local history of everything EchoType pasted or showed, newest first, saved to
/// %APPDATA%\EchoType\history.json. Nothing leaves the machine. Used by the History
/// window, the "paste last result" shortcut and the setup wizard's test step.
/// Every call happens on the UI thread.
/// </summary>
internal static class TranscriptHistory {

    public static string FilePath { get; } = Path.Combine(Settings.Dir, "history.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static List<HistoryEntry>? _entries;

    /// <summary>Raised after an entry is added, removed or the list is cleared.</summary>
    public static event Action? Changed;

    /// <summary>Raised with each newly added entry.</summary>
    public static event Action<HistoryEntry>? Added;

    public static bool Enabled { get; set; } = true;
    public static int Limit { get; set; } = 200;

    public static IReadOnlyList<HistoryEntry> Entries => Load();

    /// <summary>The newest result: from the saved list, or (with History off) kept in memory only.</summary>
    public static HistoryEntry? Latest => Enabled ? Load().FirstOrDefault() : _sessionLatest;

    private static HistoryEntry? _sessionLatest;

    public static void Add(HistoryEntry entry) {
        if (string.IsNullOrWhiteSpace(entry.Text)) {
            return;
        }
        _sessionLatest = entry;
        if (!Enabled) {
            // Not written anywhere, but "paste last" and the wizard's test step still see it.
            Added?.Invoke(entry);
            return;
        }
        var list = Load();
        list.Insert(0, entry);
        if (list.Count > Limit) {
            list.RemoveRange(Limit, list.Count - Limit);
        }
        Save();
        Added?.Invoke(entry);
        Changed?.Invoke();
    }

    public static void Remove(string id) {
        if (Load().RemoveAll(e => e.Id == id) > 0) {
            Save();
            Changed?.Invoke();
        }
    }

    public static void Clear() {
        _sessionLatest = null;
        Load().Clear();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Drops entries beyond <see cref="Limit"/> (after the limit was lowered).</summary>
    public static void Trim() {
        var list = Load();
        if (list.Count > Limit) {
            list.RemoveRange(Limit, list.Count - Limit);
            Save();
            Changed?.Invoke();
        }
    }

    private static List<HistoryEntry> Load() {
        if (_entries != null) {
            return _entries;
        }
        _entries = [];
        try {
            if (File.Exists(FilePath)) {
                _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath), JsonOptions) ?? [];
                _entries.RemoveAll(e => e == null);
                foreach (var e in _entries) {
                    e.Id ??= Guid.NewGuid().ToString("N");
                    e.Kind ??= "dictation";
                    e.App ??= "";
                    e.Model ??= "";
                    e.Transcript ??= "";
                    e.Result ??= "";
                    e.Note ??= "";
                }
            }
        } catch (Exception ex) {
            Log.Write("history: failed to load (" + ex.Message + "), starting empty");
            _entries = [];
        }
        return _entries;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(Settings.Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries ?? [], JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        } catch (Exception ex) {
            Log.Write("history: failed to save: " + ex.Message);
        }
    }

    /// <summary>Friendly label for <see cref="HistoryEntry.Kind"/>.</summary>
    public static string KindLabel(string kind) {
        if (kind.StartsWith("command", StringComparison.Ordinal)) {
            string name = kind.Length > 7 ? kind[7..].Trim(' ', '(', ')') : "";
            return name.Length > 0 ? name : Ui.Loc.T("Command");
        }
        return kind switch {
            "dictation" => Ui.Loc.T("Dictation"),
            "auto-clean" => Ui.Loc.T("Auto Clean"),
            "selection-edit" => Ui.Loc.T("Rewrite"),
            "ask-model" => Ui.Loc.T("Ask model"),
            "translate" => Ui.Loc.T("Translate"),
            "translate-selection" => Ui.Loc.T("Translation"),
            _ => kind,
        };
    }
}
