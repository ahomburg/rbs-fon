using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RbsSipFone.Models;
using RbsSipFone.Services;

namespace RbsSipFone.Views;

public partial class AccountExportDialog : Window
{
    public AccountExportDialog(IEnumerable<AccountConfig> accounts)
    {
        InitializeComponent();
        PasswordsBox.Checked += PasswordsBox_Changed;
        PasswordsBox.Unchecked += PasswordsBox_Changed;
        foreach (var account in accounts)
        {
            AccountList.Children.Add(new CheckBox
            {
                Content = string.IsNullOrWhiteSpace(account.Name) ? account.AddressOfRecord : $"{account.Name} ({account.AddressOfRecord})",
                IsChecked = true,
                Tag = account,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }
    }

    /// <summary>Pfad der geschriebenen Datei und Anzahl der Konten.</summary>
    public string? SavedPath { get; private set; }
    public int SavedCount { get; private set; }

    private void PasswordsBox_Changed(object sender, RoutedEventArgs e)
    {
        var withPasswords = PasswordsBox.IsChecked == true;
        PlainPasswordHint.Visibility = withPasswords ? Visibility.Visible : Visibility.Collapsed;
        NoPasswordHint.Visibility = withPasswords ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var selected = AccountList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Select(c => (AccountConfig)c.Tag).ToList();
        if (selected.Count == 0)
        {
            ShowError("Bitte mindestens ein Konto auswählen.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Konten exportieren",
            Filter = "Konten-Datei (*.json)|*.json",
            FileName = $"rbs-konten-{DateTime.Today:yyyy-MM-dd}.json"
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, AccountTransfer.Export(selected, PasswordsBox.IsChecked == true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Die Datei konnte nicht gespeichert werden: " + ex.Message);
            return;
        }

        SavedPath = dialog.FileName;
        SavedCount = selected.Count;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
