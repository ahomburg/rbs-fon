using System.Windows;
using RbsSipFone.Services;
using RbsSipFone.Views;

namespace RbsSipFone;

public partial class App : Application
{
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Kommandozeile: rbs-sip-fone.exe [--register | --unregister] [--autostart [--minimized]] [tel:…|callto:…|sip:…]
        if (e.Args.Any(a => a.Equals("--register", StringComparison.OrdinalIgnoreCase)))
        {
            RunRegistration(ProtocolRegistration.Register);
            return;
        }
        if (e.Args.Any(a => a.Equals("--unregister", StringComparison.OrdinalIgnoreCase)))
        {
            RunRegistration(ProtocolRegistration.Unregister);
            return;
        }
        var link = e.Args.FirstOrDefault(a => !a.StartsWith("--"));
        var autostarted = HasArg(e, Autostart.AutostartArg);

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Beim Windows-Start nichts tun, wenn das Programm schon läuft.
            if (autostarted && link == null)
            {
                Shutdown();
                return;
            }

            // Link (oder einfach "nach vorne holen") an das laufende Programm übergeben.
            var message = link != null ? SingleInstance.LinkPrefix + link : SingleInstance.ActivateMessage;
            if (!SingleInstance.Send(message))
                MessageBox.Show($"{AppInfo.DisplayName} läuft bereits, reagiert aber nicht.", AppInfo.DisplayName,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            SipLog.Write("Unbehandelter Fehler: " + args.Exception);
            MessageBox.Show("Ein unerwarteter Fehler ist aufgetreten:\n\n" + args.Exception.Message +
                            "\n\nDetails stehen im Protokoll:\n" + SipLog.FilePath,
                AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => SipLog.Write("Unbehandelter Fehler: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            SipLog.Write("Unbeobachteter Task-Fehler: " + args.Exception);
            args.SetObserved();
        };

        var window = new MainWindow();
        MainWindow = window;
        if (autostarted && link == null && HasArg(e, Autostart.MinimizedArg))
            window.WindowState = WindowState.Minimized;
        window.Show();

        _instance.Listen(message => Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            if (message.StartsWith(SingleInstance.LinkPrefix, StringComparison.Ordinal))
                window.OpenLink(message[SingleInstance.LinkPrefix.Length..]);
        }));

        if (link != null) window.OpenLink(link);

        // Wurde die Programmdatei verschoben (oder per ClickOnce aktualisiert), Registrierung und Autostart auf den
        // neuen Pfad aktualisieren. Nicht bei eigenem Konfigurationsordner, damit Test- oder portable Instanzen
        // die Einträge der installierten Version nicht übernehmen.
        if (ConfigStore.IsCustomFolder) return;
        try
        {
            if (ProtocolRegistration.IsRegistered && !ProtocolRegistration.IsUpToDate)
                ProtocolRegistration.Register();
        }
        catch (Exception ex) { SipLog.Write("Registrierung aktualisieren: " + ex.Message); }
        try
        {
            if (Autostart.IsConfigured && !Autostart.IsForCurrentPath)
                Autostart.UpdatePath();
        }
        catch (Exception ex) { SipLog.Write("Autostart aktualisieren: " + ex.Message); }
    }

    private static bool HasArg(StartupEventArgs e, string name) =>
        e.Args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void RunRegistration(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SipLog.Write("Registrierung: " + ex);
            MessageBox.Show("Die Registrierung ist fehlgeschlagen:\n\n" + ex.Message, AppInfo.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
