using System.Windows;
using RbsSipFone.ViewModels;

namespace RbsSipFone.Views;

public partial class TransferDialog : Window
{
    public TransferDialog(CallViewModel call, IReadOnlyList<CallViewModel> others)
    {
        InitializeComponent();
        CallInfo.Text = $"{call.Title} · über {call.Account.Name}";
        if (others.Count > 0)
        {
            OtherPanel.Visibility = Visibility.Visible;
            OtherBox.ItemsSource = others;
        }
        Loaded += (_, _) => TargetBox.Focus();
    }

    public TransferChoice? Result { get; private set; }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text.Trim();
        var other = OtherBox.SelectedItem as CallViewModel;

        if (target.Length == 0 && other == null)
        {
            ErrorText.Text = "Bitte ein Ziel angeben.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = target.Length > 0 ? new TransferChoice(target, null) : new TransferChoice(null, other);
        DialogResult = true;
    }
}
