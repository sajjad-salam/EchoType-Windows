using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoType;

/// <summary>
/// Settings persisted to %APPDATA%\EchoType\config.json (Settings.swift analog, minus
/// the keys that only back UI that doesn't exist in the MVP). No settings UI yet —
/// edit the file by hand and restart.
/// </summary>
internal sealed class Settings {

    [JsonPropertyName("hotkeyVk")]
    public int HotkeyVk { get; set; } = 0xA3; // VK_RCONTROL

    [JsonPropertyName("keepTranscriptOnClipboard")]
    public bool KeepTranscriptOnClipboard { get; set; } = true;

    private static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoType");

    private static string FilePath { get; } = System.IO.Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
    };

    public static Settings Load() {
        try {
            if (!File.Exists(FilePath)) {
                var defaults = new Settings();
                defaults.Save();
                return defaults;
            }
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
        } catch (Exception ex) {
            Log.Write($"settings: failed to load ({ex.Message}) — using defaults");
            return new Settings();
        }
    }

    public void Save() {
        try {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        } catch (Exception ex) {
            Log.Write("settings: failed to save: " + ex.Message);
        }
    }
}
