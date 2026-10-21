using System.Net;
using System.Net.Sockets;
using RbsSipFone.Models;
using RbsSipFone.Services;
using SIPSorcery.SIP;

namespace RbsSipFone.Sip;

/// <summary>
/// Gemeinsamer SIP-Transport für alle Konten. Nimmt neue eingehende Anrufe entgegen und
/// beantwortet Anfragen außerhalb von Dialogen (OPTIONS, NOTIFY, unbekannte BYE).
/// </summary>
public sealed class SipEngine : IDisposable
{
    /// <summary>URI-Parameter im Contact, über den eingehende INVITEs dem Konto zugeordnet werden.</summary>
    public const string InstanceParameter = "rinstance";

    public SIPTransport Transport { get; }

    /// <summary>Neue eingehende INVITE-Anfrage (noch ohne Dialog).</summary>
    public event Action<SIPRequest, SIPEndPoint>? IncomingInvite;

    /// <summary>Prüft, ob eine Call-ID zu einem bestehenden Gespräch gehört.</summary>
    public Func<string, bool> IsKnownCall { get; set; } = _ => false;

    public SipEngine(int localPort)
    {
        Transport = new SIPTransport();
        AddChannels(localPort);
        Transport.SIPTransportRequestReceived += OnRequestReceived;

        Transport.SIPRequestOutTraceEvent += (l, r, req) => SipLog.Trace($"SIP OUT {l} -> {r}\n{req}");
        Transport.SIPRequestInTraceEvent += (l, r, req) => SipLog.Trace($"SIP IN {r} -> {l}\n{req}");
        Transport.SIPResponseOutTraceEvent += (l, r, resp) => SipLog.Trace($"SIP OUT {l} -> {r}\n{resp}");
        Transport.SIPResponseInTraceEvent += (l, r, resp) => SipLog.Trace($"SIP IN {r} -> {l}\n{resp}");
    }

    private void AddChannels(int port)
    {
        try
        {
            Transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, port)));
        }
        catch (SocketException ex) when (port != 0)
        {
            SipLog.Write($"UDP-Port {port} belegt ({ex.SocketErrorCode}), verwende freien Port.");
            Transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));
        }

        try
        {
            Transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, port)));
        }
        catch (SocketException) when (port != 0)
        {
            Transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0)));
        }
        catch (Exception ex)
        {
            SipLog.Write($"TCP-Kanal nicht verfügbar: {ex.Message}");
        }

        try
        {
            // Nur ausgehende TLS-Verbindungen (kein eigenes Serverzertifikat nötig).
            Transport.AddSIPChannel(new SIPTLSChannel(new IPEndPoint(IPAddress.Any, 0)));
        }
        catch (Exception ex)
        {
            SipLog.Write($"TLS-Kanal nicht verfügbar: {ex.Message}");
        }

        foreach (var ch in Transport.GetSIPChannels())
            SipLog.Write($"SIP-Kanal: {ch.ListeningSIPEndPoint}");
    }

    private Task OnRequestReceived(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest req)
    {
        try
        {
            switch (req.Method)
            {
                case SIPMethodsEnum.INVITE when req.Header.To.ToTag == null:
                    IncomingInvite?.Invoke(req, remoteEP);
                    break;

                case SIPMethodsEnum.OPTIONS:
                case SIPMethodsEnum.NOTIFY when !IsKnownCall(req.Header.CallId):
                    // Keepalive bzw. Mailbox-Hinweise (MWI) einfach bestätigen.
                    return Transport.SendResponseAsync(SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.Ok, null));

                case SIPMethodsEnum.BYE:
                case SIPMethodsEnum.INFO:
                case SIPMethodsEnum.REFER:
                case SIPMethodsEnum.INVITE:
                    // Gehört die Anfrage zu einem Gespräch, beantwortet sie dessen SIPUserAgent.
                    if (!IsKnownCall(req.Header.CallId))
                        return Transport.SendResponseAsync(
                            SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist, null));
                    break;

                case SIPMethodsEnum.ACK:
                case SIPMethodsEnum.CANCEL:
                case SIPMethodsEnum.PRACK:
                    // Werden von der Transaktionsschicht bzw. dem Dialog behandelt.
                    break;

                default:
                    if (!IsKnownCall(req.Header.CallId))
                        return Transport.SendResponseAsync(
                            SIPResponse.GetResponse(req, SIPResponseStatusCodesEnum.MethodNotAllowed, null));
                    break;
            }
        }
        catch (Exception ex)
        {
            SipLog.Write($"Fehler bei eingehender Anfrage {req.Method}: {ex}");
        }
        return Task.CompletedTask;
    }

    /// <summary>Protokoll und URI-Schema eines Kontos.</summary>
    public static (SIPProtocolsEnum Protocol, SIPSchemesEnum Scheme) ProtocolOf(AccountConfig cfg) => cfg.Transport switch
    {
        SipTransportKind.Tcp => (SIPProtocolsEnum.tcp, SIPSchemesEnum.sip),
        SipTransportKind.Tls => (SIPProtocolsEnum.tls, SIPSchemesEnum.sips),
        _ => (SIPProtocolsEnum.udp, SIPSchemesEnum.sip)
    };

    /// <summary>Löst den optionalen Outbound-Proxy eines Kontos auf (DNS inkl. SRV).</summary>
    public async Task<SIPEndPoint?> ResolveProxyAsync(AccountConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Proxy)) return null;
        var uri = ProxyUri(cfg);
        var ep = await Transport.ResolveSIPUriAsync(uri).ConfigureAwait(false);
        if (ep == null || ep == SIPEndPoint.Empty || ep.Address == null)
            throw new InvalidOperationException($"Proxy „{cfg.Proxy}“ konnte nicht aufgelöst werden.");
        return ep;
    }

    public static SIPURI ProxyUri(AccountConfig cfg) => HostUri(cfg, cfg.Proxy.Trim());

    public static SIPURI RegistrarUri(AccountConfig cfg) =>
        HostUri(cfg, string.IsNullOrWhiteSpace(cfg.Proxy) ? cfg.Domain.Trim() : cfg.Proxy.Trim());

    private static SIPURI HostUri(AccountConfig cfg, string host)
    {
        var (protocol, scheme) = ProtocolOf(cfg);
        if (host.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || host.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            host = host[(host.IndexOf(':') + 1)..];
        return new SIPURI(null, host, null, scheme, protocol);
    }

    /// <summary>
    /// Wandelt eine Eingabe (Rufnummer, Name oder SIP-Adresse) in eine Ziel-URI für das Konto um.
    /// </summary>
    public static SIPURI BuildTargetUri(AccountConfig cfg, string input)
    {
        var (protocol, scheme) = ProtocolOf(cfg);
        var text = input.Trim();
        if (text.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return SIPURI.ParseSIPURIRelaxed(text);
        if (text.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) text = text[4..];

        if (text.Contains('@'))
            return SIPURI.ParseSIPURIRelaxed($"{(scheme == SIPSchemesEnum.sips ? "sips" : "sip")}:{text}" +
                                             (protocol == SIPProtocolsEnum.tcp ? ";transport=tcp" : ""));

        // Rufnummern: Leerzeichen, Klammern, Bindestriche und Schrägstriche entfernen.
        var user = new string(text.Where(c => !char.IsWhiteSpace(c) && c is not ('(' or ')' or '-' or '/' or '.')).ToArray());
        return new SIPURI(user, cfg.Domain.Trim(), null, scheme, protocol);
    }

    public void Dispose()
    {
        Transport.SIPTransportRequestReceived -= OnRequestReceived;
        Transport.Shutdown();
        Transport.Dispose();
    }
}
