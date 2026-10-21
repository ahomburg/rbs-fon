using System.Windows;

namespace RbsSipFone.Mvvm;

/// <summary>Leitet Aufrufe aus SIP-/Audio-Threads auf den UI-Thread um.</summary>
public static class Ui
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
