using System.Windows;
using RbsSipFone.Models;

namespace RbsSipFone.Views;

public partial class AccountDialog : Window
{
    private readonly AccountConfig _config;

    public AccountDialog(AccountConfig? existing)
    {
        InitializeComponent();
        _config = existing ?? new AccountConfig();
        Heading.Text = existing == null ? "Konto hinzufügen" : "Konto bearbeiten";

        NameBox.Text = _config.Name;
        UserBox.Text = _config.Username;
        DomainBox.Text = _config.Domain;
        PasswordBox.Password = _config.Password;
        DisplayNameBox.Text = _config.DisplayName;
        AuthUserBox.Text = _config.AuthUsername;
        ProxyBox.Text = _config.Proxy;
        TransportBox.SelectedIndex = (int)_config.Transport;
        ExpiryBox.Text = _config.Expiry.ToString();
        EnabledBox.IsChecked = _config.Enabled;

        Loaded += (_, _) => (existing == null ? NameBox : UserBox).Focus();
    }

    public AccountConfig? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim();
        var domain = DomainBox.Text.Trim();
        if (domain.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) domain = domain[4..];

        var error = AccountConfig.ValidateAddress(user, domain);

        if (!int.TryParse(ExpiryBox.Text.Trim(), out var expiry) || expiry < 60 || expiry > 3600)
            error ??= "Das Intervall muss zwischen 60 und 3600 Sekunden liegen.";

        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _config.Name = NameBox.Text.Trim();
        _config.Username = user;
        _config.Domain = domain;
        _config.Password = PasswordBox.Password;
        _config.DisplayName = DisplayNameBox.Text.Trim();
        _config.AuthUsername = AuthUserBox.Text.Trim();
        _config.Proxy = ProxyBox.Text.Trim();
        _config.Transport = (SipTransportKind)Math.Max(0, TransportBox.SelectedIndex);
        _config.Expiry = expiry;
        _config.Enabled = EnabledBox.IsChecked == true;

        Result = _config;
        DialogResult = true;
    }
}
