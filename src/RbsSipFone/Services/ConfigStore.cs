using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RbsSipFone.Models;

namespace RbsSipFone.Services;

/// <summary>Liest und schreibt Einstellungen und Anrufliste unter %APPDATA%\rbs-sip-fone.</summary>
public static class ConfigStore
{
    public const int MaxHistoryEntries = 200;

    /// <summary>Konfigurationsordner; per Umgebungsvariable RBS_SIP_FONE_HOME überschreibbar (z. B. für Tests).</summary>
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("RBS_SIP_FONE_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "rbs-sip-fone");

    /// <summary>Läuft das Programm mit eigenem Konfigurationsordner (Test- bzw. portabler Betrieb)?</summary>
    public static bool IsCustomFolder { get; } = Environment.GetEnvironmentVariable("RBS_SIP_FONE_HOME") is { Length: > 0 };

    private static string SettingsPath => Path.Combine(Folder, "settings.json");
    private static string HistoryPath => Path.Combine(Folder, "history.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();
    public static void SaveSettings(AppSettings settings) => Save(SettingsPath, settings);

    public static List<CallHistoryEntry> LoadHistory() => Load<List<CallHistoryEntry>>(HistoryPath) ?? new();
    public static void SaveHistory(IEnumerable<CallHistoryEntry> history) =>
        Save(HistoryPath, history.Take(MaxHistoryEntries).ToList());

    private static T? Load<T>(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
        }
        catch (Exception ex)
        {
            SipLog.Write($"Konnte {path} nicht lesen: {ex.Message}");
            // Defekte Datei sichern, damit sie nicht stillschweigend überschrieben wird.
            try { File.Copy(path, path + ".defekt", overwrite: true); } catch { }
            return default;
        }
    }

    private static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Folder);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
