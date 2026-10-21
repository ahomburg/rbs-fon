using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace RbsSipFone.Services;

/// <summary>
/// Sorgt dafür, dass nur eine Instanz läuft. Weitere Starts (z. B. durch einen Klick auf einen tel:-Link)
/// übergeben ihre Argumente über eine Named Pipe an die laufende Instanz. Die Pipe ist auf den aktuellen
/// Windows-Benutzer beschränkt.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ActivateMessage = "activate";
    public const string LinkPrefix = "link ";
    private const int MaxMessageLength = 2048;

    private static readonly string UserId = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;

    /// <summary>Mit eigenem Konfigurationsordner (RBS_SIP_FONE_HOME) läuft eine eigenständige Instanz.</summary>
    private static readonly string Scope =
        Environment.GetEnvironmentVariable("RBS_SIP_FONE_HOME") is { Length: > 0 } home
            ? "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.ToLowerInvariant())))[..12]
            : "";

    private static string PipeName => $"rbs-sip-fone-{UserId}{Scope}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        // "Local\" gilt je Anmeldesitzung; der Name entspricht dem der Version 1.0, damit auch diese erkannt wird.
        _mutex = new Mutex(true, $@"Local\rbs-sip-fone{Scope}", out var createdNew);
        IsFirst = createdNew;
    }

    /// <summary>Nimmt Nachrichten weiterer Instanzen entgegen (Aufruf auf einem Hintergrund-Thread).</summary>
    public void Listen(Action<string> onMessage)
    {
        if (!IsFirst) throw new InvalidOperationException("Nur die erste Instanz kann Nachrichten empfangen.");
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var buffer = new char[MaxMessageLength];
                    var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
                    var message = new string(buffer, 0, read).TrimEnd('\r', '\n');
                    if (message.Length > 0) onMessage(message);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    SipLog.Write($"Instanz-Kommunikation: {ex.Message}");
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, ct);
    }

    /// <summary>Schickt eine Nachricht an die laufende Instanz und erlaubt ihr, sich in den Vordergrund zu holen.</summary>
    public static bool Send(string message, int timeoutMs = 5000)
    {
        try
        {
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(message.Length > MaxMessageLength ? message[..MaxMessageLength] : message);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            SipLog.Write($"Laufende Instanz nicht erreichbar: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
