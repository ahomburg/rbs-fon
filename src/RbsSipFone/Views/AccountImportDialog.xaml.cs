using System.Windows;
using System.Windows.Controls;
using RbsSipFone.Models;
using RbsSipFone.Services;

namespace RbsSipFone.Views;

/// <summary>Zeigt die Konten einer Datei und lässt die zu importierenden auswählen.</summary>
public partial class AccountImportDialog : Window
{
    public AccountImportDialog(string fileName, IReadOnlyList<AccountData> accounts, IReadOnlyList<AccountConfig> existing)
    {
        InitializeComponent();
        FileText.Text = fileName;
        NoPasswordHint.Visibility = accounts.Any(a => a.Password == null) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var candidate in AccountTransfer.Prepare(accounts, existing))
        {
            var status = candidate.Error != null ? "Ungültig: " + candidate.Error
                : candidate.Existing != null ? "Bereits vorhanden. Angekreuzt wird das vorhandene Konto überschrieben."
                : "Neues Konto";
            AccountList.Children.Add(new CheckBox
            {
                Content = candidate.Data.Label,
                Tag = candidate,
                IsEnabled = candidate.Error == null,
                IsChecked = candidate.Error == null && candidate.Existing == null,
                Margin = new Thickness(0, 6, 0, 0)
            });
            AccountList.Children.Add(new TextBlock
            {
                Text = status,
                Style = (Style)FindResource("Hint"),
                Foreground = (System.Windows.Media.Brush)FindResource(candidate.Error != null ? "Danger" : "Muted"),
                Margin = new Thickness(24, 1, 0, 0)
            });
        }
        if (accounts.Count == 0) ShowError("Die Datei enthält keine Konten.");
    }

    public IReadOnlyList<ImportCandidate> Selected { get; private set; } = [];

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var selected = AccountList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Select(c => (ImportCandidate)c.Tag).ToList();
        if (selected.Count == 0)
        {
            ShowError("Bitte mindestens ein Konto auswählen.");
            return;
        }
        Selected = selected;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
