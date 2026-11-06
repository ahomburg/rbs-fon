using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RbsSipFone.Models;

namespace RbsSipFone.Services;

/// <summary>Ein SIP-Konto, wie es in einer Konten-Datei steht. Password == null: Die Datei enthält kein Passwort.</summary>
public sealed class AccountData
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    public string AuthUsername { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Proxy { get; set; } = "";
    public SipTransportKind Transport { get; set; } = SipTransportKind.Udp;
    public int Expiry { get; set; } = 300;
    public bool Enabled { get; set; } = true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; set; }

    [JsonIgnore]
    public string Address => $"{Username}@{Domain}";

    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} ({Address})";
}

/// <summary>Meldung für den Benutzer, wenn eine Datei nicht gelesen werden kann.</summary>
public sealed class AccountFileException(string message) : Exception(message);

/// <summary>Ein Konto aus der Datei: bereits vorhandenes Gegenstück und ggf. der Grund, warum es ungültig ist.</summary>
public sealed record ImportCandidate(AccountData Data, AccountConfig? Existing, string? Error);

/// <summary>
/// Export und Import von SIP-Konten als einfache, von Hand erstellbare JSON-Datei. Die Datei ist nicht verschlüsselt:
/// Passwörter stehen darin im Klartext. Importierte Dateien gelten als nicht vertrauenswürdig und werden geprüft.
/// </summary>
public static class AccountTransfer
{
    public const long MaxFileSize = 1024 * 1024;
    private const int MaxAccounts = 200;

    private const string FormatName = "rbs-sip-fone-accounts";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip, // erleichtert das Bearbeiten von Hand
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Umlaute lesbar; die Datei wird nie als HTML ausgegeben
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private sealed class FileContent
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public string? ExportedAt { get; set; }
        public List<AccountData>? Accounts { get; set; }
    }

    /// <summary>Erzeugt den Dateiinhalt, auf Wunsch mit den Passwörtern im Klartext.</summary>
    public static string Export(IEnumerable<AccountConfig> accounts, bool includePasswords)
    {
        var file = new FileContent
        {
            Format = FormatName,
            Version = FormatVersion,
            ExportedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Accounts = accounts.Select(a => new AccountData
            {
                Name = a.Name,
                DisplayName = a.DisplayName,
                Username = a.Username,
                AuthUsername = a.AuthUsername,
                Domain = a.Domain,
                Proxy = a.Proxy,
                Transport = a.Transport,
                Expiry = a.Expiry,
                Enabled = a.Enabled,
                Password = includePasswords ? a.Password : null
            }).ToList()
        };
        return JsonSerializer.Serialize(file, Json);
    }

    /// <summary>
    /// Liest den Dateiinhalt; wirft <see cref="AccountFileException"/> mit einer Meldung für den Benutzer.
    /// Pflicht ist nur die Liste „accounts“; „format“ und „version“ dürfen in selbst erstellten Dateien fehlen.
    /// </summary>
    public static IReadOnlyList<AccountData> Read(string json)
    {
        FileContent? file;
        try { file = JsonSerializer.Deserialize<FileContent>(json, Json); }
        catch (JsonException ex)
        {
            throw new AccountFileException(ex.LineNumber is { } line
                ? $"Die Datei enthält in Zeile {line + 1} einen Fehler (kein gültiges JSON oder ein unbekannter Wert)."
                : "Die Datei ist keine gültige Konten-Datei.");
        }

        if (file?.Accounts == null || (file.Format != null && file.Format != FormatName))
            throw new AccountFileException("Die Datei ist keine gültige Konten-Datei. Sie braucht eine Liste „accounts“.");
        if (file.Version > FormatVersion)
            throw new AccountFileException("Die Datei wurde mit einer neueren Programmversion erstellt. Bitte aktualisieren Sie das Programm.");
        if (file.Accounts.Count > MaxAccounts)
            throw new AccountFileException($"Die Datei enthält mehr als {MaxAccounts} Konten.");
        if (file.Accounts.Contains(null!))
            throw new AccountFileException("Die Liste „accounts“ enthält einen leeren Eintrag.");
        return file.Accounts;
    }

    /// <summary>Prüft die Konten und ordnet ihnen vorhandene zu (gleiche Adresse; bei mehreren zuerst gleiche Bezeichnung).</summary>
    public static List<ImportCandidate> Prepare(IEnumerable<AccountData> accounts, IEnumerable<AccountConfig> existing)
    {
        var items = accounts.ToList();
        var free = existing.ToList();
        var matches = new AccountConfig?[items.Count];

        static bool SameAddress(AccountData d, AccountConfig c) =>
            string.Equals(d.Address, c.AddressOfRecord, StringComparison.OrdinalIgnoreCase);

        void Assign(Func<AccountData, AccountConfig, bool> isMatch)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (matches[i] != null) continue;
                var found = free.FirstOrDefault(c => isMatch(items[i], c));
                if (found == null) continue;
                matches[i] = found;
                free.Remove(found);
            }
        }

        Assign((d, c) => SameAddress(d, c) && d.Name == c.Name);
        Assign(SameAddress);

        return items.Select((d, i) => new ImportCandidate(d, matches[i], Validate(d))).ToList();
    }

    private static string? Validate(AccountData d)
    {
        var address = AccountConfig.ValidateAddress(d.Username, d.Domain);
        if (address != null) return address;
        if (!IsToken(d.AuthUsername) || !IsToken(d.Proxy)) return "Auth-Benutzername oder Proxy enthält ungültige Zeichen.";
        return null;
    }

    private static bool IsToken(string? value) =>
        value == null || !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>
    /// Erzeugt die zu speichernde Konfiguration. Vorhandene Konten behalten ID und, falls die Datei keines enthält, ihr Passwort.
    /// Konten ohne Passwort werden deaktiviert, damit sie sich nicht vergeblich anmelden (das kann beim Anbieter zur Sperre führen).
    /// </summary>
    public static AccountConfig ToConfig(AccountData data, AccountConfig? existing)
    {
        var config = existing?.Clone() ?? new AccountConfig();
        config.Name = Clean(data.Name);
        config.DisplayName = Clean(data.DisplayName);
        config.Username = data.Username;
        config.AuthUsername = data.AuthUsername ?? "";
        config.Domain = data.Domain;
        config.Proxy = data.Proxy ?? "";
        config.Transport = data.Transport;
        config.Expiry = Math.Clamp(data.Expiry, 60, 3600);
        if (!string.IsNullOrEmpty(data.Password)) config.Password = data.Password;
        config.Enabled = data.Enabled && (data.Password != null || !string.IsNullOrEmpty(config.ProtectedPassword));
        return config;
    }

    private static string Clean(string? value) =>
        new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
}
