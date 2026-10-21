using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using RbsSipFone.Models;
using RbsSipFone.Mvvm;
using RbsSipFone.Services;
using RbsSipFone.Sip;
using SIPSorcery.SIP;

namespace RbsSipFone.ViewModels;

/// <summary>Ergebnis des Weiterleiten-Dialogs: Zielrufnummer oder anderes Gespräch.</summary>
public sealed record TransferChoice(string? Destination, CallViewModel? OtherCall);

/// <summary>Vom Hauptfenster bereitgestellte Dialoge.</summary>
public interface IDialogs
{
    AccountConfig? EditAccount(AccountConfig? existing);
    bool EditSettings(AppSettings settings);
    TransferChoice? AskTransfer(CallViewModel call, IReadOnlyList<CallViewModel> others);
    bool Confirm(string message);
    void Info(string message);
    void NotifyIncomingCall(CallViewModel call);
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxParallelCalls = 4;

    private readonly IDialogs _dialogs;
    private readonly AppSettings _settings;
    private readonly SipEngine _engine;
    private readonly TonePlayer _tones = new();
    private readonly CallerLookup _callerLookup = new();
    private readonly DispatcherTimer _clock;
    private volatile CallViewModel[] _callSnapshot = Array.Empty<CallViewModel>();
    private volatile AccountViewModel[] _accountSnapshot = Array.Empty<AccountViewModel>();

    private string _dialNumber = "";
    private AccountViewModel? _dialAccount;
    private CallViewModel? _selectedCall;

    public MainViewModel(IDialogs dialogs)
    {
        _dialogs = dialogs;
        _settings = ConfigStore.LoadSettings();
        SipLog.TraceEnabled = _settings.SipTrace;
        SipLog.Write($"{AppInfo.DisplayName} {AppInfo.Version} gestartet");

        _engine = new SipEngine(_settings.LocalSipPort);
        _engine.IncomingInvite += (req, remote) => Ui.Post(() => OnIncomingInvite(req, remote));
        _engine.IsKnownCall = callId => _callSnapshot.Any(c => c.CallId == callId);

        foreach (var cfg in _settings.Accounts) AddAccountViewModel(cfg);
        _dialAccount = Accounts.FirstOrDefault(a => a.Config.Id == _settings.DefaultAccountId) ?? Accounts.FirstOrDefault(a => a.Enabled);

        foreach (var h in ConfigStore.LoadHistory()) History.Add(h);

        Calls.CollectionChanged += (_, _) =>
        {
            _callSnapshot = Calls.ToArray();
            OnPropertyChanged(nameof(HasCalls));
            OnPropertyChanged(nameof(ShowCallsInCenter));
            OnPropertyChanged(nameof(DialButtonText));
        };
        Accounts.CollectionChanged += (_, _) =>
        {
            _accountSnapshot = Accounts.ToArray();
            OnPropertyChanged(nameof(HasAccounts));
            OnPropertyChanged(nameof(RegistrationSummary));
        };
        _accountSnapshot = Accounts.ToArray();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => { foreach (var c in Calls) c.Tick(); };
        _clock.Start();

        DialCommand = new RelayCommand(_ => _ = DialAsync(), _ => CanDial);
        KeyCommand = new RelayCommand(p => PressKey(p as string));
        BackspaceCommand = new RelayCommand(() => { if (DialNumber.Length > 0) DialNumber = DialNumber[..^1]; });
        AddAccountCommand = new RelayCommand(AddAccount);
        EditAccountCommand = new RelayCommand(p => EditAccount(p as AccountViewModel));
        DeleteAccountCommand = new RelayCommand(p => DeleteAccount(p as AccountViewModel));
        SettingsCommand = new RelayCommand(OpenSettings);
        ToggleViewCommand = new RelayCommand(() => IsMiniView = !IsMiniView);
        AnswerCommand = new RelayCommand(p => _ = AnswerAsync(p as CallViewModel));
        RejectCommand = new RelayCommand(p => (p as CallViewModel)?.Reject());
        HangupCommand = new RelayCommand(p => (p as CallViewModel)?.Hangup());
        HoldCommand = new RelayCommand(p => _ = ToggleHoldAsync(p as CallViewModel));
        MuteCommand = new RelayCommand(p => _ = (p as CallViewModel)?.ToggleMuteAsync());
        TransferCommand = new RelayCommand(p => _ = TransferAsync(p as CallViewModel));
        CallHistoryCommand = new RelayCommand(p => _ = CallBackAsync(p as CallHistoryEntry));
        PickHistoryCommand = new RelayCommand(p => { if (p is CallHistoryEntry h) DialNumber = h.RemoteNumber; });
        DeleteHistoryCommand = new RelayCommand(p => { if (p is CallHistoryEntry h) { History.Remove(h); SaveHistory(); } });
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => History.Count > 0);

        foreach (var acc in Accounts) acc.Start();
    }

    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    public ObservableCollection<CallViewModel> Calls { get; } = new();
    public ObservableCollection<CallHistoryEntry> History { get; } = new();

    public ICommand DialCommand { get; }
    public ICommand KeyCommand { get; }
    public ICommand BackspaceCommand { get; }
    public ICommand AddAccountCommand { get; }
    public ICommand EditAccountCommand { get; }
    public ICommand DeleteAccountCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ToggleViewCommand { get; }
    public ICommand AnswerCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand HangupCommand { get; }
    public ICommand HoldCommand { get; }
    public ICommand MuteCommand { get; }
    public ICommand TransferCommand { get; }
    public ICommand CallHistoryCommand { get; }
    public ICommand PickHistoryCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public bool HasAccounts => Accounts.Count > 0;
    public bool HasCalls => Calls.Count > 0;

    /// <summary>Mini-Ansicht: nur Wählfeld und laufende Gespräche, ohne Konten und Anrufliste.</summary>
    public bool IsMiniView
    {
        get => _settings.MiniView;
        set
        {
            if (_settings.MiniView == value) return;
            _settings.MiniView = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowCallsInCenter));
            SaveSettings();
        }
    }

    /// <summary>In der Mini-Ansicht stehen die Gespräche über dem Wählfeld.</summary>
    public bool ShowCallsInCenter => IsMiniView && HasCalls;

    public string RegistrationSummary
    {
        get
        {
            var enabled = Accounts.Count(a => a.Enabled);
            if (Accounts.Count == 0) return "Kein Konto eingerichtet";
            var ok = Accounts.Count(a => a.IsRegistered);
            return enabled == 0 ? "Alle Konten deaktiviert" : $"{ok} von {enabled} Konten angemeldet";
        }
    }

    public string DialNumber
    {
        get => _dialNumber;
        set
        {
            // CanExecute neu auswerten, auch wenn die Nummer programmatisch gesetzt wird.
            if (Set(ref _dialNumber, value ?? "")) CommandManager.InvalidateRequerySuggested();
        }
    }

    public AccountViewModel? DialAccount
    {
        get => _dialAccount;
        set
        {
            if (!Set(ref _dialAccount, value) || value == null) return;
            CommandManager.InvalidateRequerySuggested();
            _settings.DefaultAccountId = value.Config.Id;
            SaveSettings();
        }
    }

    public CallViewModel? SelectedCall
    {
        get => _selectedCall;
        set
        {
            if (_selectedCall != null) _selectedCall.IsSelected = false;
            if (!Set(ref _selectedCall, value)) return;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged(nameof(DtmfHint));
        }
    }

    /// <summary>Gespräch, an das Tastendrücke als DTMF gehen.</summary>
    private CallViewModel? DtmfTarget =>
        SelectedCall is { IsLive: true } ? SelectedCall : Calls.FirstOrDefault(c => c.IsLive);

    public string DtmfHint => DtmfTarget is { } c ? $"Tonwahl an {c.Title}" : "";

    public string DialButtonText => "Anrufen";

    private bool CanDial => DialAccount != null && !string.IsNullOrWhiteSpace(DialNumber);

    // ------------------------------------------------------------------ Wählen

    private void PressKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        var c = key[0];
        var target = DtmfTarget;
        if (target != null)
        {
            _ = target.SendDtmfAsync(c);
            _tones.PlayDtmf(c, SpeakerIndex());
            return;
        }
        DialNumber += key;
    }

    /// <summary>Tastatureingabe aus dem Fenster (Ziffern, *, #) – sendet DTMF im Gespräch.</summary>
    public bool HandleTypedKey(char c)
    {
        if (!"0123456789*#".Contains(c)) return false;
        var target = DtmfTarget;
        if (target == null) return false;
        _ = target.SendDtmfAsync(c);
        _tones.PlayDtmf(c, SpeakerIndex());
        return true;
    }

    private async Task DialAsync()
    {
        var account = DialAccount;
        var number = DialNumber.Trim();
        if (account == null || number.Length == 0) return;

        if (!account.Enabled)
        {
            _dialogs.Info($"Das Konto „{account.Name}“ ist deaktiviert.");
            return;
        }
        if (Calls.Count(c => !c.IsEnded) >= MaxParallelCalls)
        {
            _dialogs.Info("Es sind bereits zu viele Gespräche offen.");
            return;
        }

        SIPURI target;
        try { target = SipEngine.BuildTargetUri(account.Config, number); }
        catch (Exception ex)
        {
            _dialogs.Info("Ungültige Rufnummer oder SIP-Adresse: " + ex.Message);
            return;
        }

        if (_settings.AutoHoldOtherCalls) await HoldAllExceptAsync(null);

        var call = CallViewModel.CreateOutgoing(account, _engine, target, number, MicIndex, SpeakerIndex);
        AddCall(call);
        DialNumber = "";
        await call.DialAsync(target);
    }

    /// <summary>
    /// Übernimmt einen Link (tel:, callto:, sip:, sips:) ins Wählfeld und wählt je nach Einstellung sofort.
    /// </summary>
    public bool HandleExternalLink(string link)
    {
        var target = PhoneLink.ToDialString(link);
        if (target == null)
        {
            var shown = link.Length > 80 ? link[..80] + "…" : link;
            SipLog.Write($"Link nicht verwendbar: {shown}");
            _dialogs.Info($"Der Link „{shown}“ enthält keine gültige Rufnummer oder SIP-Adresse.");
            return false;
        }

        SipLog.Write($"Link übernommen: {target}");
        DialNumber = target;
        if (_settings.DialLinksImmediately) _ = DialWhenReadyAsync();
        return true;
    }

    /// <summary>Beim Programmstart per Link kurz auf die Anmeldung des Kontos warten, dann wählen.</summary>
    private async Task DialWhenReadyAsync()
    {
        for (var i = 0; i < 25 && DialAccount?.State == RegistrationState.Registering; i++)
            await Task.Delay(200);
        if (!string.IsNullOrWhiteSpace(DialNumber)) await DialAsync();
    }

    private Task CallBackAsync(CallHistoryEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.RemoteNumber) || entry.RemoteNumber == "Unterdrückt")
            return Task.CompletedTask;
        var account = Accounts.FirstOrDefault(a => a.Config.Id == entry.AccountId && a.Enabled) ?? DialAccount;
        if (account != null && account != DialAccount) DialAccount = account;
        DialNumber = entry.RemoteNumber;
        return DialAsync();
    }

    // ------------------------------------------------------------------ eingehend

    private void OnIncomingInvite(SIPRequest invite, SIPEndPoint remote)
    {
        var account = FindAccountFor(invite);
        if (account == null)
        {
            SipLog.Write($"Eingehender Anruf von {remote} ohne passendes Konto abgewiesen ({invite.URI})");
            _ = _engine.Transport.SendResponseAsync(SIPResponse.GetResponse(invite, SIPResponseStatusCodesEnum.NotFound, null));
            return;
        }

        if (Calls.Count(c => !c.IsEnded) >= MaxParallelCalls)
        {
            _ = _engine.Transport.SendResponseAsync(SIPResponse.GetResponse(invite, SIPResponseStatusCodesEnum.BusyHere, null));
            return;
        }

        SipLog.Write($"[{account.Name}] eingehender Anruf von {invite.Header.From?.FromURI}");
        var call = CallViewModel.CreateIncoming(account, _engine, invite, MicIndex, SpeakerIndex);
        AddCall(call);
        if (SelectedCall == null || SelectedCall.IsEnded) SelectedCall = call;
        _dialogs.NotifyIncomingCall(call);
        if (CallerLookup.IsConfigured(_settings.CallerLookupUrl) && CallerLookup.IsLookupable(call.RemoteNumber))
            _ = LookupCallerAsync(call);
    }

    /// <summary>Fragt den Namen beim Anrufer-Dienst ab und zeigt ihn an, solange das Gespräch noch läuft.</summary>
    private async Task LookupCallerAsync(CallViewModel call)
    {
        try
        {
            var result = await _callerLookup.LookupAsync(_settings.CallerLookupUrl!, _settings.CallerLookupApiKey, call.RemoteNumber);
            if (result.Name == null) return;
            call.SetLookedUpName(result.Name);
            if (!call.IsEnded) return;

            // Anruf war schon vorbei (z. B. kurz geklingelt): Namen im Eintrag der Anrufliste nachtragen.
            var entry = History.FirstOrDefault(h => h.Start == call.Started && h.RemoteNumber == call.RemoteNumber);
            if (entry == null || entry.RemoteName == result.Name) return;
            var index = History.IndexOf(entry);
            entry.RemoteName = result.Name;
            History.RemoveAt(index);
            History.Insert(index, entry);
            SaveHistory();
        }
        catch (Exception ex) { SipLog.Write("Anrufer-Suche: " + ex.Message); }
    }

    /// <summary>
    /// Ordnet ein INVITE einem Konto zu: zuerst über den rinstance-Parameter der Request-URI,
    /// dann über Benutzer/Domain in Request-URI bzw. To-Header.
    /// </summary>
    private AccountViewModel? FindAccountFor(SIPRequest invite)
    {
        var accounts = _accountSnapshot.Where(a => a.Enabled).ToList();
        if (accounts.Count == 0) return null;

        var instance = invite.URI.Parameters.Get(SipEngine.InstanceParameter);
        var byInstance = accounts.FirstOrDefault(a => a.Config.Id == instance);
        if (byInstance != null) return byInstance;

        static bool Eq(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        var toUri = invite.Header.To?.ToURI;

        return accounts.FirstOrDefault(a => Eq(a.Config.Username, toUri?.User) && Eq(a.Config.Domain, toUri?.HostAddress))
            ?? accounts.FirstOrDefault(a => Eq(a.Config.Username, invite.URI.User))
            ?? accounts.FirstOrDefault(a => Eq(a.Config.Username, toUri?.User))
            ?? (accounts.Count == 1 ? accounts[0] : accounts.FirstOrDefault(a => a.IsRegistered));
    }

    private async Task AnswerAsync(CallViewModel? call)
    {
        if (call == null || !call.IsIncoming) return;
        if (_settings.AutoHoldOtherCalls) await HoldAllExceptAsync(call);
        SelectedCall = call;
        await call.AnswerAsync();
    }

    // ------------------------------------------------------------------ Gesprächsliste

    private void AddCall(CallViewModel call)
    {
        call.StateChanged += _ => UpdateTones();
        call.StateChanged += _ => OnPropertyChanged(nameof(DtmfHint));
        call.Ended += OnCallEnded;
        Calls.Insert(0, call);
        SelectedCall ??= call;
        UpdateTones();
    }

    private void OnCallEnded(CallViewModel call)
    {
        History.Insert(0, new CallHistoryEntry
        {
            Direction = call.Direction,
            Missed = !call.WasConnected,
            RemoteName = call.RemoteName,
            RemoteNumber = call.RemoteNumber,
            AccountId = call.Account.Config.Id,
            AccountName = call.Account.Name,
            Start = call.Started,
            Duration = call.Duration
        });
        while (History.Count > ConfigStore.MaxHistoryEntries) History.RemoveAt(History.Count - 1);
        SaveHistory();
        UpdateTones();

        // Beendetes Gespräch noch kurz mit Grund anzeigen, dann entfernen.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Calls.Remove(call);
            if (SelectedCall == call) SelectedCall = Calls.FirstOrDefault(c => !c.IsEnded);
        };
        timer.Start();
    }

    private async Task ToggleHoldAsync(CallViewModel? call)
    {
        if (call == null || !call.IsActive) return;
        if (call.IsOnHold && _settings.AutoHoldOtherCalls) await HoldAllExceptAsync(call);
        await call.SetHoldAsync(!call.IsOnHold);
    }

    private async Task HoldAllExceptAsync(CallViewModel? keep)
    {
        foreach (var other in Calls.Where(c => c != keep && c.IsLive).ToList())
            await other.SetHoldAsync(true);
    }

    private async Task TransferAsync(CallViewModel? call)
    {
        if (call == null || !call.IsActive) return;
        var others = Calls.Where(c => c != call && c.IsActive).ToList();
        var choice = _dialogs.AskTransfer(call, others);
        if (choice == null) return;

        try
        {
            var ok = choice.OtherCall != null
                ? await call.AttendedTransferAsync(choice.OtherCall)
                : !string.IsNullOrWhiteSpace(choice.Destination) && await call.BlindTransferAsync(choice.Destination!);
            if (!ok) _dialogs.Info("Die Weiterleitung wurde vom Server oder der Gegenstelle nicht angenommen.");
        }
        catch (Exception ex)
        {
            SipLog.Write($"Weiterleitung fehlgeschlagen: {ex}");
            _dialogs.Info("Weiterleitung fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>Klingelton, Anklopfton oder Freiton je nach Gesprächslage.</summary>
    private void UpdateTones()
    {
        var open = Calls.Where(c => !c.IsEnded).ToList();
        if (open.Any(c => c.IsIncoming))
        {
            if (open.Any(c => c.IsLive)) _tones.Play(ToneKind.CallWaiting, SpeakerIndex());
            else _tones.Play(ToneKind.Ring, AudioDevices.SpeakerIndex(_settings.RingDevice ?? _settings.SpeakerDevice));
        }
        else if (open.Any(c => c.NeedsRingback))
        {
            _tones.Play(ToneKind.Ringback, SpeakerIndex());
        }
        else
        {
            _tones.Stop();
        }
    }

    private int MicIndex() => AudioDevices.MicrophoneIndex(_settings.MicrophoneDevice);
    private int SpeakerIndex() => AudioDevices.SpeakerIndex(_settings.SpeakerDevice);

    // ------------------------------------------------------------------ Konten

    private AccountViewModel AddAccountViewModel(AccountConfig cfg)
    {
        var vm = new AccountViewModel(cfg, _engine);
        vm.EnabledToggled += _ => { SaveSettings(); OnPropertyChanged(nameof(RegistrationSummary)); };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AccountViewModel.State) or "") OnPropertyChanged(nameof(RegistrationSummary));
        };
        Accounts.Add(vm);
        return vm;
    }

    private void AddAccount()
    {
        var cfg = _dialogs.EditAccount(null);
        if (cfg == null) return;
        _settings.Accounts.Add(cfg);
        SaveSettings();
        var vm = AddAccountViewModel(cfg);
        DialAccount ??= vm;
        vm.Start();
    }

    private void EditAccount(AccountViewModel? vm)
    {
        if (vm == null) return;
        var cfg = _dialogs.EditAccount(vm.Config.Clone());
        if (cfg == null) return;
        var index = _settings.Accounts.FindIndex(a => a.Id == cfg.Id);
        if (index >= 0) _settings.Accounts[index] = cfg; else _settings.Accounts.Add(cfg);
        SaveSettings();
        vm.Replace(cfg);
        OnPropertyChanged(nameof(RegistrationSummary));
    }

    private void DeleteAccount(AccountViewModel? vm)
    {
        if (vm == null) return;
        if (Calls.Any(c => c.Account == vm && !c.IsEnded))
        {
            _dialogs.Info("Über dieses Konto läuft noch ein Gespräch.");
            return;
        }
        if (!_dialogs.Confirm($"Konto „{vm.Name}“ wirklich entfernen?")) return;
        vm.Stop();
        Accounts.Remove(vm);
        _settings.Accounts.RemoveAll(a => a.Id == vm.Config.Id);
        if (DialAccount == vm) DialAccount = Accounts.FirstOrDefault(a => a.Enabled) ?? Accounts.FirstOrDefault();
        SaveSettings();
    }

    // ------------------------------------------------------------------ Einstellungen

    private void OpenSettings()
    {
        var oldPort = _settings.LocalSipPort;
        if (!_dialogs.EditSettings(_settings)) return;
        SipLog.TraceEnabled = _settings.SipTrace;
        _callerLookup.ClearCache();
        SaveSettings();
        if (oldPort != _settings.LocalSipPort)
            _dialogs.Info("Der geänderte SIP-Port wird nach einem Neustart des Programms wirksam.");
    }

    private void ClearHistory()
    {
        if (!_dialogs.Confirm("Anrufliste vollständig löschen?")) return;
        History.Clear();
        SaveHistory();
    }

    private void SaveSettings()
    {
        try { ConfigStore.SaveSettings(_settings); }
        catch (Exception ex) { _dialogs.Info("Einstellungen konnten nicht gespeichert werden: " + ex.Message); }
    }

    private void SaveHistory()
    {
        try { ConfigStore.SaveHistory(History); }
        catch (Exception ex) { SipLog.Write("Anrufliste speichern: " + ex.Message); }
    }

    public bool HasOpenCalls => Calls.Any(c => !c.IsEnded);

    public void Dispose()
    {
        _clock.Stop();
        _tones.Dispose();
        foreach (var call in Calls.Where(c => !c.IsEnded).ToList()) call.Hangup();
        foreach (var acc in Accounts) acc.Stop();
        // Abmeldungen (REGISTER mit Expires 0) kurz Zeit zum Versand geben.
        Thread.Sleep(400);
        _engine.Dispose();
        SipLog.Write($"{AppInfo.DisplayName} beendet");
    }
}
