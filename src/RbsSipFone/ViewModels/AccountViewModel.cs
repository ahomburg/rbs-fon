using System.Net;
using RbsSipFone.Models;
using RbsSipFone.Mvvm;
using RbsSipFone.Services;
using RbsSipFone.Sip;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace RbsSipFone.ViewModels;

public enum RegistrationState { Disabled, Registering, Registered, Failed }

/// <summary>Ein SIP-Konto mit seiner Registrierung beim Server.</summary>
public sealed class AccountViewModel : ObservableObject
{
    private const string UserAgentName = "rbs-sip-fone/1.0";

    private readonly SipEngine _engine;
    private SIPRegistrationUserAgent? _registration;
    private CancellationTokenSource? _startCts;
    private RegistrationState _state = RegistrationState.Disabled;
    private string _statusText = "Deaktiviert";

    public AccountViewModel(AccountConfig config, SipEngine engine)
    {
        Config = config;
        _engine = engine;
    }

    public AccountConfig Config { get; private set; }

    /// <summary>Wird gesetzt, wenn ein Outbound-Proxy konfiguriert ist.</summary>
    public SIPEndPoint? OutboundProxy { get; private set; }

    /// <summary>Wird ausgelöst, wenn der Benutzer das Konto per Schalter aktiviert/deaktiviert.</summary>
    public event Action<AccountViewModel>? EnabledToggled;

    public string Name => string.IsNullOrWhiteSpace(Config.Name) ? Config.AddressOfRecord : Config.Name;
    public string Address => Config.AddressOfRecord;
    public string TransportLabel => Config.Transport.ToString().ToUpperInvariant();

    public bool Enabled
    {
        get => Config.Enabled;
        set
        {
            if (Config.Enabled == value) return;
            Config.Enabled = value;
            OnPropertyChanged();
            if (value) Start(); else Stop();
            EnabledToggled?.Invoke(this);
        }
    }

    public RegistrationState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value)) OnPropertyChanged(nameof(IsRegistered));
        }
    }

    public bool IsRegistered => State == RegistrationState.Registered;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public void Replace(AccountConfig config)
    {
        Stop();
        Config = config;
        OnPropertyChanged(string.Empty);
        if (config.Enabled) Start();
    }

    public void Start()
    {
        Stop();
        if (!Config.Enabled) return;
        _startCts = new CancellationTokenSource();
        _ = StartAsync(_startCts.Token);
    }

    private async Task StartAsync(CancellationToken ct)
    {
        State = RegistrationState.Registering;
        StatusText = "Anmeldung läuft …";
        var cfg = Config;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                OutboundProxy = await _engine.ResolveProxyAsync(cfg);
                if (ct.IsCancellationRequested) return;
                CreateRegistration(cfg);
                return;
            }
            catch (Exception ex)
            {
                SipLog.Write($"[{Name}] Start fehlgeschlagen: {ex.Message}");
                State = RegistrationState.Failed;
                StatusText = ex.Message + " – neuer Versuch in 60 s";
            }

            try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void CreateRegistration(AccountConfig cfg)
    {
        var (protocol, scheme) = SipEngine.ProtocolOf(cfg);
        var aor = new SIPURI(cfg.Username, cfg.Domain.Trim(), null, scheme);

        // Contact mit 0.0.0.0 wird vom Transport durch die tatsächliche lokale Adresse ersetzt.
        // Der rinstance-Parameter ordnet eingehende Anrufe eindeutig diesem Konto zu.
        var contact = new SIPURI(scheme, IPAddress.Any, 0) { User = cfg.Username };
        contact.Protocol = protocol;
        contact.Parameters.Set(SipEngine.InstanceParameter, cfg.Id);

        var authUser = string.IsNullOrWhiteSpace(cfg.AuthUsername) ? cfg.Username : cfg.AuthUsername;
        var registrar = SipEngine.RegistrarUri(cfg).ToString();

        var reg = new SIPRegistrationUserAgent(_engine.Transport, OutboundProxy, aor, authUser, cfg.Password,
            null, registrar, contact, Math.Clamp(cfg.Expiry, 60, 3600), null,
            maxRegistrationAttemptTimeout: 30, registerFailureRetryInterval: 60, maxRegisterAttempts: 3,
            exitOnUnequivocalFailure: false)
        {
            UserAgent = UserAgentName,
            UserDisplayName = string.IsNullOrWhiteSpace(cfg.DisplayName) ? null : cfg.DisplayName
        };

        reg.RegistrationSuccessful += (_, _) => Ui.Post(() =>
        {
            if (_registration != reg) return;
            State = RegistrationState.Registered;
            StatusText = "Angemeldet";
            SipLog.Write($"[{Name}] registriert");
        });
        reg.RegistrationTemporaryFailure += (_, resp, msg) => Ui.Post(() =>
        {
            if (_registration != reg) return;
            State = RegistrationState.Failed;
            StatusText = Describe(resp, msg) + " – neuer Versuch folgt";
            SipLog.Write($"[{Name}] temporärer Fehler: {msg}");
        });
        reg.RegistrationFailed += (_, resp, msg) => Ui.Post(() =>
        {
            if (_registration != reg) return;
            State = RegistrationState.Failed;
            StatusText = Describe(resp, msg);
            SipLog.Write($"[{Name}] Registrierung fehlgeschlagen: {msg}");
        });
        reg.RegistrationRemoved += (_, _) => Ui.Post(() =>
        {
            if (_registration != reg) return;
            State = RegistrationState.Registering;
            StatusText = "Registrierung entfernt – erneute Anmeldung …";
        });

        _registration = reg;
        SipLog.Write($"[{Name}] registriere {aor} über {registrar}" + (OutboundProxy != null ? $" (Proxy {OutboundProxy})" : ""));
        reg.Start();
    }

    private static string Describe(SIPResponse? resp, string? msg)
    {
        if (resp == null) return string.IsNullOrWhiteSpace(msg) ? "Server nicht erreichbar" : Translate(msg);
        return resp.StatusCode switch
        {
            401 or 403 or 407 => $"Zugangsdaten abgelehnt ({resp.StatusCode})",
            404 => "Benutzer unbekannt (404)",
            408 => "Zeitüberschreitung (408)",
            503 => "Dienst nicht verfügbar (503)",
            _ => $"{resp.StatusCode} {resp.ReasonPhrase}"
        };
    }

    private static string Translate(string msg) =>
        msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "Zeitüberschreitung – Server antwortet nicht"
        : msg.Contains("resolve", StringComparison.OrdinalIgnoreCase) ? "Server-Adresse nicht auflösbar"
        : msg;

    public void Stop()
    {
        _startCts?.Cancel();
        _startCts = null;
        var reg = _registration;
        _registration = null;
        if (reg != null)
        {
            try { reg.Stop(); } catch (Exception ex) { SipLog.Write($"[{Name}] Abmelden: {ex.Message}"); }
        }
        State = RegistrationState.Disabled;
        StatusText = Config.Enabled ? "Getrennt" : "Deaktiviert";
    }
}
