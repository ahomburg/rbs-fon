using System.IO;

namespace RbsSipFone.Services;

/// <summary>Einfaches Protokoll nach %APPDATA%\rbs-sip-fone\sip.log (wird beim Start ab 5 MB rotiert).</summary>
public static class SipLog
{
    private static readonly object Gate = new();
    public static string FilePath { get; } = Path.Combine(ConfigStore.Folder, "sip.log");

    /// <summary>Wenn aktiv, werden komplette SIP-Nachrichten mitgeschrieben.</summary>
    public static bool TraceEnabled { get; set; }

    static SipLog()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Folder);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024) File.Move(FilePath, FilePath + ".1", overwrite: true);
        }
        catch { }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}"); }
            catch { }
        }
    }

    public static void Trace(string message)
    {
        if (TraceEnabled) Write(message);
    }
}
