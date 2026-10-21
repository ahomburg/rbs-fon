using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RbsSipFone.Models;
using RbsSipFone.Services;
using RbsSipFone.ViewModels;

namespace RbsSipFone.Views;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
        PreviewTextInput += OnPreviewTextInput;
        Loaded += (_, _) => NumberBox.Focus();

        ApplyViewMode(_vm.IsMiniView);
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsMiniView)) ApplyViewMode(_vm.IsMiniView);
        };
    }

    // ------------------------------------------------------------------ Mini-/Vollansicht

    private const double FullMinWidth = 980, FullMinHeight = 620, MiniMinWidth = 360, MiniMinHeight = 520;
    private Size _fullSize = new(1140, 740);
    private Size _miniSize = new(420, 820);
    private bool? _appliedMini;

    /// <summary>
    /// Mini-Ansicht: Konten (links) und Anrufliste (rechts) ausblenden, Gespräche über dem Wählfeld zeigen.
    /// Die Fenstergröße wird je Ansicht gemerkt.
    /// </summary>
    private void ApplyViewMode(bool mini)
    {
        if (_appliedMini == mini) return;
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        if (_appliedMini.HasValue && IsLoaded)
        {
            if (_appliedMini.Value) _miniSize = new Size(ActualWidth, ActualHeight);
            else _fullSize = new Size(ActualWidth, ActualHeight);
        }

        AccountsColumn.Width = mini ? new GridLength(0) : new GridLength(300);
        DialColumn.Width = mini ? new GridLength(1, GridUnitType.Star) : new GridLength(340);
        CallsColumn.Width = mini ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        AccountsPanel.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        CallsPanel.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;

        ViewToggleIcon.Text = mini ? "" : "";
        ViewToggleText.Text = "Mini";
        ViewToggleText.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        ViewToggleButton.ToolTip = mini ? "Volle Ansicht (Strg+M)" : "Mini-Ansicht (Strg+M)";
        System.Windows.Automation.AutomationProperties.SetName(ViewToggleButton, mini ? "Volle Ansicht" : "Mini-Ansicht");

        // Erst die Mindestgröße anpassen, dann die Größe der Ansicht setzen (auf den Arbeitsbereich begrenzt).
        var work = SystemParameters.WorkArea;
        var size = mini ? _miniSize : _fullSize;
        MinWidth = mini ? MiniMinWidth : FullMinWidth;
        MinHeight = mini ? MiniMinHeight : FullMinHeight;
        Width = Math.Min(Math.Max(size.Width, MinWidth), work.Width);
        Height = Math.Min(Math.Max(size.Height, MinHeight), work.Height);
        if (!double.IsNaN(Left) && Left + Width > work.Right) Left = Math.Max(work.Left, work.Right - Width);
        if (!double.IsNaN(Top) && Top + Height > work.Bottom) Top = Math.Max(work.Top, work.Bottom - Height);

        _appliedMini = mini;
    }

    /// <summary>Ziffern, * und # außerhalb des Wählfelds gehen als DTMF an das laufende Gespräch.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (NumberBox.IsKeyboardFocusWithin || e.Text.Length != 1) return;
        if (_vm.HandleTypedKey(e.Text[0])) e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_vm.HasOpenCalls && !Confirm("Es laufen noch Gespräche. Trotzdem beenden und alle Gespräche trennen?"))
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Übernimmt einen tel:/callto:/sip:-Link (beim Start oder von einer weiteren Instanz).</summary>
    public void OpenLink(string link)
    {
        BringToFront();
        if (_vm.HandleExternalLink(link))
        {
            NumberBox.Focus();
            NumberBox.CaretIndex = NumberBox.Text.Length;
        }
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Kurz "Topmost" erzwingen, falls Windows die Aktivierung nur in der Taskleiste anzeigt.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && ItemsControl.ContainerFromElement(HistoryList, src) is ListBoxItem { DataContext: CallHistoryEntry entry })
            _vm.CallHistoryCommand.Execute(entry);
    }

    private void CopyNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CallHistoryEntry entry } && !string.IsNullOrEmpty(entry.RemoteNumber))
        {
            try { Clipboard.SetText(entry.RemoteNumber); } catch { }
        }
    }

    // ------------------------------------------------------------------ IDialogs

    public AccountConfig? EditAccount(AccountConfig? existing)
    {
        var dlg = new AccountDialog(existing) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public bool EditSettings(AppSettings settings)
    {
        var dlg = new SettingsDialog(settings) { Owner = this };
        return dlg.ShowDialog() == true;
    }

    public TransferChoice? AskTransfer(CallViewModel call, IReadOnlyList<CallViewModel> others)
    {
        var dlg = new TransferDialog(call, others) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    public bool Confirm(string message) =>
        MessageBox.Show(this, message, AppInfo.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string message) =>
        MessageBox.Show(this, message, AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);

    public void NotifyIncomingCall(CallViewModel call)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsActive) FlashTaskbar();
    }

    private void FlashTaskbar()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = uint.MaxValue,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}
