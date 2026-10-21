using Microsoft.Win32;

namespace RbsSipFone.Services;

/// <summary>
/// Startet rbs-sip-fone nach der Windows-Anmeldung (Run-Schlüssel des aktuellen Benutzers, keine Admin-Rechte).
/// Ob minimiert gestartet wird, steht im Startbefehl selbst. Hat der Benutzer den Eintrag in Windows
/// (Task-Manager bzw. Einstellungen → Apps → Autostart) abgeschaltet, gilt der Autostart als aus.
/// </summary>
public static class Autostart
{
    public const string AutostartArg = "--autostart";
    public const string MinimizedArg = "--minimized";

    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = ProtocolRegistration.AppName;

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");

    private static string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return key?.GetValue(ValueName) as string;
        }
    }

    /// <summary>Gibt es einen Autostart-Eintrag (unabhängig davon, ob Windows ihn ausführt)?</summary>
    public static bool IsConfigured => Command != null;

    /// <summary>Wird rbs-sip-fone tatsächlich beim Windows-Start gestartet?</summary>
    public static bool IsEnabled => IsConfigured && !IsDisabledByUser;

    /// <summary>Eintrag vorhanden, aber in den Windows-Autostart-Einstellungen abgeschaltet.</summary>
    public static bool IsDisabledByUser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedPath);
            // Erstes Byte gerade (02, 06) = an, ungerade (03, 07) = vom Benutzer abgeschaltet.
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
    }

    public static bool StartsMinimized => Command?.Contains(MinimizedArg, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Zeigt der Eintrag auf genau diese Programmdatei?</summary>
    public static bool IsForCurrentPath =>
        Command?.StartsWith($"\"{ExePath}\"", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Schaltet den Autostart ein – auch wenn er zuvor in Windows abgeschaltet wurde.</summary>
    public static void Enable(bool minimized)
    {
        Write(minimized);
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true))
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        SipLog.Write($"Autostart eingeschaltet{(minimized ? " (minimiert)" : "")}: {ExePath}");
    }

    public static void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunPath, writable: true))
            run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedPath, writable: true))
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        SipLog.Write("Autostart ausgeschaltet");
    }

    /// <summary>Trägt nach Verschieben oder Update den neuen Programmpfad ein; die Windows-Einstellung bleibt unberührt.</summary>
    public static void UpdatePath()
    {
        Write(StartsMinimized);
        SipLog.Write($"Autostart auf neuen Programmpfad aktualisiert: {ExePath}");
    }

    private static void Write(bool minimized)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunPath);
        run.SetValue(ValueName, $"\"{ExePath}\" {AutostartArg}{(minimized ? " " + MinimizedArg : "")}");
    }
}
