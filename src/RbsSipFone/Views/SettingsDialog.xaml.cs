using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RbsSipFone.Models;
using RbsSipFone.Services;

namespace RbsSipFone.Views;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;

    public SettingsDialog(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        Fill(MicBox, AudioDevices.Microphones(), settings.MicrophoneDevice);
        var speakers = AudioDevices.Speakers();
        Fill(SpeakerBox, speakers, settings.SpeakerDevice);
        Fill(RingBox, speakers, settings.RingDevice ?? settings.SpeakerDevice);

        PortBox.Text = settings.LocalSipPort.ToString();
        AutoHoldBox.IsChecked = settings.AutoHoldOtherCalls;
        TraceBox.IsChecked = settings.SipTrace;
        DialLinksBox.IsChecked = settings.DialLinksImmediately;
        UpdateLinkStatus();

        SubtitleText.Text = $"{AppInfo.DisplayName} {AppInfo.Version}";
        LookupUrlBox.Text = settings.CallerLookupUrl ?? "";
        LookupKeyBox.Password = settings.CallerLookupApiKey;

        AutostartBox.IsChecked = Autostart.IsEnabled;
        AutostartMinimizedBox.IsChecked = !Autostart.IsConfigured || Autostart.StartsMinimized;
        AutostartHint.Text = Autostart.IsDisabledByUser
            ? "Der Autostart wurde in den Windows-Einstellungen (Apps → Autostart) abgeschaltet. Mit dem Häkchen hier wird er wieder eingeschaltet."
            : $"Nach der Anmeldung an Windows wird {AppInfo.DisplayName} gestartet, damit Ihre Konten sofort erreichbar sind.";
    }

    /// <summary>Überträgt die Autostart-Auswahl in Windows; false bei Fehler.</summary>
    private bool ApplyAutostart()
    {
        var wanted = AutostartBox.IsChecked == true;
        var minimized = AutostartMinimizedBox.IsChecked == true;
        try
        {
            // Nur bei einer echten Änderung schreiben – sonst würde z. B. eine Test- oder portable Instanz
            // den Eintrag der installierten Version auf ihren eigenen Pfad umbiegen.
            if (wanted && (!Autostart.IsEnabled || Autostart.StartsMinimized != minimized))
                Autostart.Enable(minimized);
            else if (!wanted && Autostart.IsEnabled)
                Autostart.Disable();
            return true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Autostart konnte nicht geändert werden: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void UpdateLinkStatus()
    {
        var registered = ProtocolRegistration.IsRegistered;
        LinkDot.Fill = (System.Windows.Media.Brush)FindResource(registered ? "Success" : "LineStrong");
        LinkStatus.Text = registered ? "Bei Windows angemeldet" : "Nicht bei Windows angemeldet";
        LinkHint.Text = registered
            ? $"Wählen Sie „{AppInfo.DisplayName}“ in den Windows-Einstellungen unter „Standard-Apps“ für TEL, CALLTO und SIP aus."
            : $"Nach dem Anmelden kann {AppInfo.DisplayName} in Windows als Standard-App für Telefon-Links gewählt werden, z. B. für Links wie tel:+49721….";
        RegisterButton.Content = registered ? "Anmeldung entfernen" : "Bei Windows anmelden";
        DefaultAppsButton.Visibility = registered ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ProtocolRegistration.IsRegistered)
            {
                ProtocolRegistration.Unregister();
            }
            else
            {
                ProtocolRegistration.Register();
                // Windows erlaubt Programmen nicht, sich selbst als Standard zu setzen – Einstellungen direkt öffnen.
                ProtocolRegistration.OpenDefaultAppsSettings();
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Registrierung fehlgeschlagen: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        UpdateLinkStatus();
    }

    private void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try { ProtocolRegistration.OpenDefaultAppsSettings(); }
        catch (Exception ex)
        {
            ErrorText.Text = "Einstellungen konnten nicht geöffnet werden: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private static void Fill(ComboBox box, List<string> devices, string? selected)
    {
        if (!string.IsNullOrEmpty(selected) && !devices.Contains(selected)) devices.Add(selected + " (nicht verbunden)");
        box.ItemsSource = devices;
        box.SelectedItem = devices.FirstOrDefault(d => d == selected) ?? devices.FirstOrDefault(d => d.StartsWith(selected ?? "\0")) ?? devices[0];
    }

    private static string? Selected(ComboBox box)
    {
        var value = box.SelectedItem as string;
        if (value == null || value == AudioDevices.DefaultDevice) return null;
        return value.Replace(" (nicht verbunden)", "");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port < 0 || port > 65535)
        {
            ShowError("Bitte einen gültigen Port (0–65535) eingeben.", PortBox);
            return;
        }
        if (CallerLookup.Validate(LookupUrlBox.Text) is { } urlError)
        {
            ShowError(urlError, LookupUrlBox);
            return;
        }
        if (!ApplyAutostart()) return;

        _settings.MicrophoneDevice = Selected(MicBox);
        _settings.SpeakerDevice = Selected(SpeakerBox);
        // Explizit speichern (auch "Windows-Standard"), damit null weiterhin "wie Lautsprecher" bedeutet.
        _settings.RingDevice = (RingBox.SelectedItem as string)?.Replace(" (nicht verbunden)", "");
        _settings.LocalSipPort = port;
        _settings.AutoHoldOtherCalls = AutoHoldBox.IsChecked == true;
        _settings.SipTrace = TraceBox.IsChecked == true;
        _settings.DialLinksImmediately = DialLinksBox.IsChecked == true;
        _settings.CallerLookupUrl = string.IsNullOrWhiteSpace(LookupUrlBox.Text) ? null : LookupUrlBox.Text.Trim();
        _settings.CallerLookupApiKey = LookupKeyBox.Password.Trim();
        DialogResult = true;
    }

    /// <summary>Zeigt einen Fehler in der Fußzeile und wechselt auf den Reiter des betroffenen Felds.</summary>
    private void ShowError(string message, Control? field = null)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        if (field == null) return;
        for (DependencyObject? d = field; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
        {
            if (d is TabItem tab) { tab.IsSelected = true; break; }
        }
        Dispatcher.BeginInvoke(() => field.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private async void LookupTest_Click(object sender, RoutedEventArgs e)
    {
        var url = LookupUrlBox.Text.Trim();
        var number = LookupTestNumberBox.Text.Trim();
        if (!CallerLookup.IsConfigured(url)) { ShowTestResult("Bitte zuerst die Service-URL eintragen.", false); return; }
        if (CallerLookup.Validate(url) is { } urlError) { ShowTestResult(urlError, false); return; }
        if (!CallerLookup.IsLookupable(number)) { ShowTestResult("Bitte eine Rufnummer zum Testen eingeben.", false); return; }

        LookupTestButton.IsEnabled = false;
        ShowTestResult("Frage den Dienst ab …", null);
        try
        {
            var key = LookupKeyBox.Password.Trim();
            var result = await new CallerLookup().LookupAsync(url, key, number, useCache: false);
            var text = result.Name != null ? $"„{result.Name}“ – {result.Info}" : result.Info;
            if (CallerLookup.SendsKeyInsecurely(url, key))
                text += "\nHinweis: Der API-Key wird über http unverschlüsselt übertragen – https wird empfohlen.";
            ShowTestResult(text, result.Name != null);
        }
        catch (Exception ex) { ShowTestResult("Fehler: " + ex.Message, false); }
        finally { LookupTestButton.IsEnabled = true; }
    }

    private void ShowTestResult(string text, bool? success)
    {
        LookupTestResult.Text = text;
        LookupTestResult.Foreground = (System.Windows.Media.Brush)FindResource(success switch
        {
            true => "Success",
            false => "Danger",
            null => "Muted"
        });
        LookupTestResult.Visibility = Visibility.Visible;
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigStore.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigStore.Folder}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
