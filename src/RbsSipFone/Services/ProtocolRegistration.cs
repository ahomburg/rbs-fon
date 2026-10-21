using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RbsSipFone.Services;

/// <summary>
/// Meldet das Programm bei Windows als Programm für tel:, callto:, sip: und sips: an (nur für den aktuellen Benutzer).
/// Windows lässt die eigentliche Auswahl als Standard-App nur den Benutzer in den Einstellungen treffen.
/// </summary>
public static class ProtocolRegistration
{
    /// <summary>Technischer Name der Anmeldung (Schlüssel in RegisteredApplications).</summary>
    public const string AppName = "rbs-sip-fone";

    private const string CapabilitiesPath = @"Software\rbs-sip-fone\Capabilities";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";
    private const string ApplicationsPath = @"Software\Classes\Applications\rbs-sip-fone.exe";
    /// <summary>Kennzeichnet Protokollschlüssel, die von rbs-sip-fone selbst angelegt wurden.</summary>
    private const string OwnerMarker = "rbs-sip-fone";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
    private static string OpenCommand => $"\"{ExePath}\" \"%1\"";
    private static string ProgId(string scheme) => $"rbs-sip-fone.Url.{scheme}";

    /// <summary>Ist das Programm als Kandidat für die Telefon-Links angemeldet?</summary>
    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath);
            return key?.GetValue(AppName) != null;
        }
    }

    /// <summary>Zeigt die Anmeldung auf genau diese Programmdatei und trägt sie den aktuellen Programmnamen?</summary>
    public static bool IsUpToDate
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId("tel")}\shell\open\command");
            using var caps = Registry.CurrentUser.OpenSubKey(CapabilitiesPath);
            return IsRegistered
                && string.Equals(key?.GetValue(null) as string, OpenCommand, StringComparison.OrdinalIgnoreCase)
                && caps?.GetValue("ApplicationName") as string == AppInfo.DisplayName;
        }
    }

    public static void Register()
    {
        var icon = $"\"{ExePath}\",0";

        foreach (var scheme in PhoneLink.Schemes)
        {
            using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId(scheme)}"))
            {
                progId.SetValue(null, $"{scheme.ToUpperInvariant()}-Link ({AppInfo.DisplayName})");
                progId.SetValue("FriendlyTypeName", $"{scheme.ToUpperInvariant()}-Link ({AppInfo.DisplayName})");
                using (var di = progId.CreateSubKey("DefaultIcon")) di.SetValue(null, icon);
                using (var cmd = progId.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, OpenCommand);
            }

            // Nur falls das Protokoll Windows noch gar nicht bekannt ist, einen Protokollschlüssel anlegen.
            using var existing = Registry.ClassesRoot.OpenSubKey(scheme);
            if (existing == null)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                key.SetValue(null, $"URL:{scheme}");
                key.SetValue("URL Protocol", "");
                key.SetValue(OwnerMarker, "1");
                using (var di = key.CreateSubKey("DefaultIcon")) di.SetValue(null, icon);
                using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue(null, OpenCommand);
            }
        }

        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            caps.SetValue("ApplicationName", AppInfo.DisplayName);
            caps.SetValue("ApplicationDescription", "SIP-Telefon (Softphone) mit mehreren Konten");
            caps.SetValue("ApplicationIcon", icon);
            using var urls = caps.CreateSubKey("URLAssociations");
            foreach (var scheme in PhoneLink.Schemes) urls.SetValue(scheme, ProgId(scheme));
        }

        using (var app = Registry.CurrentUser.CreateSubKey(ApplicationsPath))
        {
            app.SetValue("FriendlyAppName", AppInfo.DisplayName);
            using var cmd = app.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, OpenCommand);
        }

        using (var reg = Registry.CurrentUser.CreateSubKey(RegisteredApplicationsPath))
            reg.SetValue(AppName, CapabilitiesPath);

        NotifyShell();
        SipLog.Write($"Als Programm für Telefon-Links registriert: {ExePath}");
    }

    public static void Unregister()
    {
        using (var reg = Registry.CurrentUser.OpenSubKey(RegisteredApplicationsPath, writable: true))
            reg?.DeleteValue(AppName, throwOnMissingValue: false);

        Registry.CurrentUser.DeleteSubKeyTree(@"Software\rbs-sip-fone", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ApplicationsPath, throwOnMissingSubKey: false);

        foreach (var scheme in PhoneLink.Schemes)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId(scheme)}", throwOnMissingSubKey: false);

            bool ours;
            using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{scheme}"))
                ours = key?.GetValue(OwnerMarker) != null;
            if (ours) Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{scheme}", throwOnMissingSubKey: false);
        }

        NotifyShell();
        SipLog.Write("Registrierung für Telefon-Links entfernt");
    }

    /// <summary>Öffnet die Windows-Einstellungen „Standard-Apps“, unter Windows 11 direkt bei diesem Programm.</summary>
    public static void OpenDefaultAppsSettings()
    {
        var uri = Environment.OSVersion.Version.Build >= 22000
            ? $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(AppName)}"
            : "ms-settings:defaultapps";
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
    }

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
