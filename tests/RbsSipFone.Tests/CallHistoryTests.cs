using RbsSipFone.Models;
using Xunit;

namespace RbsSipFone.Tests;

public class CallHistoryTests
{
    private static CallHistoryEntry Entry(bool missed, bool busy) => new()
    {
        Direction = CallDirection.Incoming,
        Missed = missed,
        RejectedBusy = busy,
        RemoteName = "Testanrufer",
        RemoteNumber = "0721999",
        AccountName = "Büro",
        Start = DateTime.Today.AddHours(15)
    };

    [Fact]
    public void Busy_rejected_call_is_labelled_besetzt() =>
        Assert.Equal("0721999 · 15:00 · Büro · besetzt", Entry(missed: true, busy: true).Details);

    [Fact]
    public void Missed_call_stays_verpasst() =>
        Assert.Equal("0721999 · 15:00 · Büro · verpasst", Entry(missed: true, busy: false).Details);

    [Fact]
    public void Busy_option_is_on_by_default_also_for_old_settings_files()
    {
        Assert.True(new AppSettings().BusyWhenInCall);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"LocalSipPort\":5060}")!;
        Assert.True(old.BusyWhenInCall);
    }
}
