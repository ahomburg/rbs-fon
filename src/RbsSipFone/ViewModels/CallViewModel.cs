using RbsSipFone.Models;
using RbsSipFone.Mvvm;
using RbsSipFone.Services;
using RbsSipFone.Sip;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Windows;

namespace RbsSipFone.ViewModels;

public enum CallState { None, Incoming, Dialing, Ringing, Connected, Ended }

/// <summary>Ein einzelnes Gespräch (ein SIPUserAgent mit eigener Audio-Session).</summary>
public sealed class CallViewModel : ObservableObject
{
    private readonly SipEngine _engine;
    private readonly Func<int> _micIndex;
    private readonly Func<int> _speakerIndex;
    private readonly string _initialCallId;
    private SIPServerUserAgent? _uas;
    private WindowsAudioEndPoint? _audio;
    private VoIPMediaSession? _media;

    private CallState _state;
    private string _stateText = "";
    private bool _isMuted, _isOnHold, _isRemoteHold, _hasEarlyMedia, _isSelected;
    private DateTime? _connectedAt;
    private bool _busyAction;
    private bool _localHangup;
    private readonly SemaphoreSlim _dtmfLock = new(1, 1);

    private CallViewModel(AccountViewModel account, SipEngine engine, CallDirection direction,
        Func<int> micIndex, Func<int> speakerIndex, string callId)
    {
        Account = account;
        _engine = engine;
        Direction = direction;
        _micIndex = micIndex;
        _speakerIndex = speakerIndex;
        _initialCallId = callId;
        Started = DateTime.Now;
        UA = new SIPUserAgent(engine.Transport, account.OutboundProxy);
        // Feuert auch beim eigenen Auflegen – dann gewinnt der Grund aus Hangup().
        UA.OnCallHungup += _ => Ui.Post(() => End(_localHangup ? "Aufgelegt" : "Gegenseite hat aufgelegt"));
        UA.RemotePutOnHold += () => Ui.Post(() => IsRemoteHold = true);
        UA.RemoteTookOffHold += () => Ui.Post(() => IsRemoteHold = false);
    }

    public SIPUserAgent UA { get; }
    public AccountViewModel Account { get; }
    public CallDirection Direction { get; }
    public DateTime Started { get; }
    public string RemoteName { get; private set; } = "";
    public string RemoteNumber { get; private set; } = "";
    public bool WasConnected => _connectedAt.HasValue;
    public TimeSpan Duration => _connectedAt.HasValue ? DateTime.Now - _connectedAt.Value : TimeSpan.Zero;
    public string? EndReason { get; private set; }

    public string CallId => UA.Dialogue?.CallId ?? _initialCallId;

    public event Action<CallViewModel>? StateChanged;
    public event Action<CallViewModel>? Ended;

    /// <summary>Übernimmt den von der Anrufer-Erkennung gefundenen Namen.</summary>
    public void SetLookedUpName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == RemoteName) return;
        RemoteName = name;
        OnPropertyChanged(nameof(RemoteName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }

    public string Title => string.IsNullOrWhiteSpace(RemoteName) ? RemoteNumber : RemoteName;
    public string Subtitle => string.IsNullOrWhiteSpace(RemoteName) || RemoteName == RemoteNumber ? "" : RemoteNumber;
    public string DirectionGlyph => Direction == CallDirection.Incoming ? "" : "";

    public CallState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsIncoming));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsOutgoingPending));
            OnPropertyChanged(nameof(IsEnded));
            UpdateStateText();
            StateChanged?.Invoke(this);
        }
    }

    public bool IsIncoming => State == CallState.Incoming;
    public bool IsOutgoingPending => State is CallState.Dialing or CallState.Ringing;
    public bool IsActive => State == CallState.Connected;
    public bool IsEnded => State == CallState.Ended;
    /// <summary>Gespräch, dessen Audio gerade läuft (verbunden und nicht gehalten).</summary>
    public bool IsLive => State == CallState.Connected && !IsOnHold;
    public bool NeedsRingback => State == CallState.Ringing && !_hasEarlyMedia;

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => Set(ref _isMuted, value);
    }

    public bool IsOnHold
    {
        get => _isOnHold;
        private set
        {
            if (!Set(ref _isOnHold, value)) return;
            OnPropertyChanged(nameof(IsLive));
            UpdateStateText();
            StateChanged?.Invoke(this);
        }
    }

    public bool IsRemoteHold
    {
        get => _isRemoteHold;
        private set
        {
            if (Set(ref _isRemoteHold, value)) UpdateStateText();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string DurationText
    {
        get
        {
            var d = Duration;
            return d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
        }
    }

    /// <summary>Vom Sekundentakt des Hauptfensters aufgerufen.</summary>
    public void Tick()
    {
        if (State == CallState.Connected) OnPropertyChanged(nameof(DurationText));
    }

    private void UpdateStateText()
    {
        StateText = State switch
        {
            CallState.Incoming => "Eingehender Anruf",
            CallState.Dialing => "Verbindung wird aufgebaut …",
            CallState.Ringing => "Es klingelt …",
            CallState.Connected when IsOnHold => "Gehalten",
            CallState.Connected when IsRemoteHold => "Von Gegenseite gehalten",
            CallState.Connected => "Verbunden",
            _ => EndReason ?? "Beendet"
        };
    }

    // ---------------------------------------------------------------- ausgehend

    public static CallViewModel CreateOutgoing(AccountViewModel account, SipEngine engine, SIPURI target,
        string displayNumber, Func<int> micIndex, Func<int> speakerIndex)
    {
        var call = new CallViewModel(account, engine, CallDirection.Outgoing, micIndex, speakerIndex, CallProperties.CreateNewCallId())
        {
            RemoteNumber = displayNumber,
        };
        call.State = CallState.Dialing;
        return call;
    }

    public async Task DialAsync(SIPURI target)
    {
        var cfg = Account.Config;
        var (_, scheme) = SipEngine.ProtocolOf(cfg);
        var authUser = string.IsNullOrWhiteSpace(cfg.AuthUsername) ? cfg.Username : cfg.AuthUsername;
        var from = new SIPFromHeader(cfg.DisplayName, new SIPURI(cfg.Username, cfg.Domain.Trim(), null, scheme), null);

        var descriptor = new SIPCallDescriptor(authUser, cfg.Password, target.ToString(), from.ToString(),
            target.ToString(), null, null, authUser, SIPCallDirection.Out, SDP.SDP_MIME_CONTENTTYPE, null, null)
        {
            CallId = _initialCallId
        };

        UA.ClientCallTrying += (_, _) => Ui.Post(() => { if (State == CallState.Dialing) UpdateStateText(); });
        UA.ClientCallRinging += (_, resp) => Ui.Post(() =>
        {
            if (State is not (CallState.Dialing or CallState.Ringing)) return;
            _hasEarlyMedia = resp?.Status == SIPResponseStatusCodesEnum.SessionProgress && !string.IsNullOrEmpty(resp.Body);
            State = CallState.Ringing;
            StateChanged?.Invoke(this);
        });
        UA.ClientCallFailed += (_, error, resp) => Ui.Post(() => End(DescribeFailure(error, resp)));

        try
        {
            CreateMedia();
            SipLog.Write($"[{Account.Name}] Anruf an {target}");
            var answered = await UA.Call(descriptor, _media);
            Ui.Post(() =>
            {
                if (answered && State != CallState.Ended) Connected();
                else End(EndReason ?? "Nicht erreicht");
            });
        }
        catch (Exception ex)
        {
            SipLog.Write($"[{Account.Name}] Anruf fehlgeschlagen: {ex}");
            Ui.Post(() => End("Fehler: " + ex.Message));
        }
    }

    private static string DescribeFailure(string? error, SIPResponse? resp)
    {
        if (resp == null) return string.IsNullOrWhiteSpace(error) ? "Nicht erreicht" : error;
        return resp.StatusCode switch
        {
            404 or 484 => "Rufnummer nicht erreichbar",
            408 or 480 => "Teilnehmer nicht erreichbar",
            486 or 600 => "Besetzt",
            487 => "Abgebrochen",
            401 or 403 or 407 => "Nicht berechtigt",
            603 => "Abgelehnt",
            _ => $"{resp.StatusCode} {resp.ReasonPhrase}"
        };
    }

    // ---------------------------------------------------------------- eingehend

    public static CallViewModel CreateIncoming(AccountViewModel account, SipEngine engine, SIPRequest invite,
        Func<int> micIndex, Func<int> speakerIndex)
    {
        var call = new CallViewModel(account, engine, CallDirection.Incoming, micIndex, speakerIndex, invite.Header.CallId);
        var from = invite.Header.From;
        call.RemoteNumber = from?.FromURI?.User ?? from?.FromURI?.Host ?? "Unbekannt";
        call.RemoteName = from?.FromName?.Trim('"', ' ') ?? "";
        if (string.Equals(call.RemoteNumber, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            call.RemoteNumber = "Unterdrückt";
        }

        call._uas = call.UA.AcceptCall(invite);
        call.UA.ServerCallCancelled += (_, _) => Ui.Post(() => call.End("Anrufer hat aufgelegt"));
        call._uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);
        call.State = CallState.Incoming;
        return call;
    }

    public async Task AnswerAsync()
    {
        if (State != CallState.Incoming || _uas == null || _busyAction) return;
        _busyAction = true;
        try
        {
            CreateMedia();
            var ok = await UA.Answer(_uas, _media);
            if (ok && State != CallState.Ended) Connected();
            else End("Annahme fehlgeschlagen");
        }
        catch (Exception ex)
        {
            SipLog.Write($"[{Account.Name}] Annehmen fehlgeschlagen: {ex}");
            End("Fehler: " + ex.Message);
        }
        finally { _busyAction = false; }
    }

    public void Reject()
    {
        if (State != CallState.Incoming || _uas == null) return;
        try { _uas.Reject(SIPResponseStatusCodesEnum.BusyHere, null); } catch (Exception ex) { SipLog.Write(ex.Message); }
        End("Abgelehnt");
    }

    // ---------------------------------------------------------------- im Gespräch

    private void CreateMedia()
    {
        _audio = new WindowsAudioEndPoint(new AudioEncoder(), _speakerIndex(), _micIndex());
        _audio.OnAudioSourceError += err => SipLog.Write($"Mikrofon-Fehler: {err}");
        _audio.OnAudioSinkError += err => SipLog.Write($"Lautsprecher-Fehler: {err}");
        _media = new VoIPMediaSession(_audio.ToMediaEndPoints()) { AcceptRtpFromAny = true };
    }

    private void Connected()
    {
        _connectedAt ??= DateTime.Now;
        State = CallState.Connected;
        OnPropertyChanged(nameof(DurationText));
        SipLog.Write($"[{Account.Name}] verbunden mit {RemoteNumber}");
    }

    public void Hangup()
    {
        try
        {
            switch (State)
            {
                case CallState.Incoming:
                    _uas?.Reject(SIPResponseStatusCodesEnum.Decline, null);
                    End("Abgelehnt");
                    return;
                case CallState.Dialing:
                case CallState.Ringing:
                    UA.Cancel();
                    End("Abgebrochen");
                    return;
                case CallState.Connected:
                    _localHangup = true;
                    UA.Hangup();
                    End("Aufgelegt");
                    return;
            }
        }
        catch (Exception ex)
        {
            SipLog.Write($"Auflegen: {ex.Message}");
            End("Aufgelegt");
        }
    }

    public async Task SetHoldAsync(bool hold)
    {
        if (State != CallState.Connected || hold == IsOnHold || _busyAction) return;
        _busyAction = true;
        try
        {
            if (hold)
            {
                UA.PutOnHold();
                if (_audio != null) await _audio.Pause();
            }
            else
            {
                UA.TakeOffHold();
                if (_audio != null)
                {
                    await _audio.Resume();
                    if (IsMuted) await _audio.PauseAudio();
                }
            }
            IsOnHold = hold;
        }
        catch (Exception ex)
        {
            SipLog.Write($"Halten: {ex.Message}");
        }
        finally { _busyAction = false; }
    }

    public async Task ToggleMuteAsync()
    {
        if (State != CallState.Connected || _audio == null) return;
        try
        {
            if (IsMuted)
            {
                if (!IsOnHold) await _audio.ResumeAudio();
                IsMuted = false;
            }
            else
            {
                await _audio.PauseAudio();
                IsMuted = true;
            }
        }
        catch (Exception ex) { SipLog.Write($"Stummschalten: {ex.Message}"); }
    }

    /// <summary>Sendet einen DTMF-Ton (RFC 4733). Töne werden nacheinander gesendet, damit keiner verloren geht.</summary>
    public async Task SendDtmfAsync(char key)
    {
        byte? tone = key switch
        {
            >= '0' and <= '9' => (byte)(key - '0'),
            '*' => 10,
            '#' => 11,
            _ => null
        };
        if (tone == null) return;

        await _dtmfLock.WaitAsync();
        try
        {
            if (State == CallState.Connected) await UA.SendDtmf(tone.Value);
        }
        catch (Exception ex) { SipLog.Write($"DTMF: {ex.Message}"); }
        finally { _dtmfLock.Release(); }
    }

    /// <summary>Blindes Weiterleiten an eine Rufnummer / SIP-Adresse.</summary>
    public async Task<bool> BlindTransferAsync(string destination)
    {
        if (State != CallState.Connected) return false;
        var target = SipEngine.BuildTargetUri(Account.Config, destination);
        var cfg = Account.Config;
        var authUser = string.IsNullOrWhiteSpace(cfg.AuthUsername) ? cfg.Username : cfg.AuthUsername;
        SipLog.Write($"[{Account.Name}] Weiterleitung an {target}");
        var ok = await UA.BlindTransfer(target, TimeSpan.FromSeconds(15), CancellationToken.None, null, authUser, cfg.Password);
        if (ok) End("Weitergeleitet an " + destination);
        return ok;
    }

    /// <summary>Verbindet dieses Gespräch mit einem anderen (vermittelte Übergabe).</summary>
    public async Task<bool> AttendedTransferAsync(CallViewModel other)
    {
        if (State != CallState.Connected || other.State != CallState.Connected || other.UA.Dialogue == null) return false;
        var cfg = Account.Config;
        var authUser = string.IsNullOrWhiteSpace(cfg.AuthUsername) ? cfg.Username : cfg.AuthUsername;
        SipLog.Write($"[{Account.Name}] Vermittle {RemoteNumber} an {other.RemoteNumber}");
        var ok = await UA.AttendedTransfer(other.UA.Dialogue, TimeSpan.FromSeconds(15), CancellationToken.None, null, authUser, cfg.Password);
        if (ok)
        {
            End("Vermittelt an " + other.Title);
            other.End("Vermittelt an " + Title);
        }
        return ok;
    }

    public void End(string reason)
    {
        if (State == CallState.Ended) return;
        EndReason = reason;
        State = CallState.Ended;
        UpdateStateText();

        var media = _media;
        var audio = _audio;
        _media = null;
        _audio = null;
        _ = Task.Run(async () =>
        {
            try { media?.Close(reason); } catch { }
            try { if (audio != null) await audio.Close(); } catch { }
            try { UA.Dispose(); } catch { }
        });

        SipLog.Write($"[{Account.Name}] Gespräch mit {RemoteNumber} beendet: {reason}");
        Ended?.Invoke(this);
    }
}
