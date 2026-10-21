using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RbsSipFone.Models;

public enum SipTransportKind { Udp, Tcp, Tls }

/// <summary>Gespeicherte Konfiguration eines SIP-Kontos.</summary>
public sealed class AccountConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    /// <summary>Bezeichnung des Kontos im Programm, z. B. "Büro".</summary>
    public string Name { get; set; } = "";
    /// <summary>Name, der beim Angerufenen erscheint (From-Header).</summary>
    public string DisplayName { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Optionaler abweichender Benutzername für die Authentifizierung.</summary>
    public string AuthUsername { get; set; } = "";
    public string Domain { get; set; } = "";
    /// <summary>Optionaler Registrar / Outbound-Proxy (host[:port]).</summary>
    public string Proxy { get; set; } = "";
    public SipTransportKind Transport { get; set; } = SipTransportKind.Udp;
    public int Expiry { get; set; } = 300;
    public bool Enabled { get; set; } = true;
    public string ProtectedPassword { get; set; } = "";

    [JsonIgnore]
    public string Password
    {
        get => Secret.Unprotect(ProtectedPassword);
        set => ProtectedPassword = Secret.Protect(value);
    }

    [JsonIgnore]
    public string AddressOfRecord => $"{Username}@{Domain}";

    public AccountConfig Clone() => (AccountConfig)MemberwiseClone();
}

public sealed class AppSettings
{
    public List<AccountConfig> Accounts { get; set; } = new();
    public string? DefaultAccountId { get; set; }
    /// <summary>Lokaler SIP-Port (UDP/TCP). 0 = automatisch.</summary>
    public int LocalSipPort { get; set; } = 5060;
    public string? MicrophoneDevice { get; set; }
    public string? SpeakerDevice { get; set; }
    public string? RingDevice { get; set; }
    public bool AutoHoldOtherCalls { get; set; } = true;
    /// <summary>Bei Klick auf tel:/sip:-Links sofort wählen statt die Nummer nur ins Wählfeld zu übernehmen.</summary>
    public bool DialLinksImmediately { get; set; }
    public bool SipTrace { get; set; }
    /// <summary>Kompakte Ansicht (nur Wählfeld und Gespräche).</summary>
    public bool MiniView { get; set; }

    /// <summary>REST-Dienst zur Anrufer-Erkennung (GET); leer = aus. Platzhalter {number} und {apikey}.</summary>
    public string? CallerLookupUrl { get; set; }
    /// <summary>API-Key des Dienstes, mit DPAPI verschlüsselt.</summary>
    public string ProtectedCallerLookupApiKey { get; set; } = "";

    [JsonIgnore]
    public string CallerLookupApiKey
    {
        get => Secret.Unprotect(ProtectedCallerLookupApiKey);
        set => ProtectedCallerLookupApiKey = Secret.Protect(value);
    }
}

public enum CallDirection { Incoming, Outgoing }

public sealed class CallHistoryEntry
{
    public CallDirection Direction { get; set; }
    public bool Missed { get; set; }
    public string RemoteName { get; set; } = "";
    public string RemoteNumber { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public DateTime Start { get; set; }
    public TimeSpan Duration { get; set; }

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(RemoteName) ? RemoteNumber : RemoteName;

    [JsonIgnore]
    public string Details
    {
        get
        {
            var when = Start.Date == DateTime.Today ? Start.ToString("HH:mm")
                     : Start.Date == DateTime.Today.AddDays(-1) ? "gestern " + Start.ToString("HH:mm")
                     : Start.ToString("dd.MM. HH:mm");
            var parts = new List<string> { when, AccountName };
            if (!string.IsNullOrWhiteSpace(RemoteName) && RemoteName != RemoteNumber) parts.Insert(0, RemoteNumber);
            if (Missed) parts.Add(Direction == CallDirection.Incoming ? "verpasst" : "nicht erreicht");
            else if (Duration > TimeSpan.Zero) parts.Add(Duration.ToString(Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"));
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    /// <summary>Icon-Glyph (Segoe MDL2 Assets).</summary>
    [JsonIgnore]
    public string Glyph => Missed ? "" : Direction == CallDirection.Incoming ? "" : "";
}

/// <summary>Verschlüsselt Passwörter benutzergebunden mit Windows DPAPI.</summary>
internal static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("rbs-sip-fone");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
}
