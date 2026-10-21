# ringring best sipfon (rbs)

Einfaches, voll funktionsfähiges SIP-Telefon (Softphone) für Windows. Gestaltet im Erscheinungsbild des
**rbs Redaktionsbüro Stutensee**: Orange `#fc790d`, Cyan `#0693e3`, Anthrazit `#242424`, Schrift Inter, rbs-Logo.

Technisch heißt das Programm weiterhin `rbs-sip-fone` (Programmdatei, Datenordner, Windows-Einträge), damit
bestehende Installationen, Einstellungen und Anmeldungen erhalten bleiben.

C# / WPF auf .NET 10, SIP-Stack: [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery), Audio: [NAudio](https://github.com/naudio/NAudio).
Lizenz: MIT für den eigenen Code. Für Logo und Marke sowie für die Bibliotheken gelten Ausnahmen, siehe [Lizenz](#lizenz).

> **Kein Notruf-Telefon:** rbs ist nicht für zuverlässige Notrufe (110/112) ausgelegt. Notrufe hängen
> vom SIP-Anbieter, der Internetverbindung, der Stromversorgung und dem laufenden PC ab, und der Standort wird nicht
> übermittelt. Für Notrufe bitte ein Festnetz- oder Mobiltelefon verwenden.

## Funktionen

- **Mehrere SIP-Konten gleichzeitig** – alle Konten sind parallel angemeldet und erreichbar.
  Eingehende Anrufe werden über einen `rinstance`-Parameter im Contact eindeutig dem richtigen Konto zugeordnet.
- Kontoverwaltung: Bezeichnung, Benutzername, Domain, Passwort, Anzeigename, abweichender Auth-Benutzer,
  Registrar/Outbound-Proxy, Transport **UDP / TCP / TLS**, Registrierungsintervall, aktiv/inaktiv per Schalter.
- Registrierungsstatus je Konto (angemeldet / Anmeldung läuft / Fehler mit Ursache), automatische Neuversuche.
- Ausgehende Anrufe über ein wählbares Konto (Rufnummer, `name@domain` oder `sip:`-URI), Enter wählt.
- Eingehende Anrufe mit Klingelton, Anklopfton während eines Gesprächs, blinkender Taskleiste.
- Im Gespräch: **Halten/Fortsetzen**, **Stummschalten**, **DTMF** (RFC 4733, Wähltasten oder Tastatur),
  **Weiterleiten** (blind an eine Nummer oder vermittelt an ein anderes laufendes Gespräch), Auflegen.
- Mehrere Gespräche parallel (bis 4); beim Annehmen/Wählen werden andere Gespräche automatisch gehalten (abschaltbar).
- Lokal erzeugter deutscher Freiton (425 Hz) bei 180 Ringing ohne Early Media.
- Anrufliste (ein-/ausgehend/verpasst, Dauer, Konto) mit Rückruf per Doppelklick und Kontextmenü.
- Audiogeräte wählbar: Mikrofon, Lautsprecher/Headset, separates Klingelton-Gerät.
- Optionales SIP-Protokoll zur Fehlersuche.
- **Standard-App für Telefon-Links** (`tel:`, `callto:`, `sip:`, `sips:`), z. B. Links auf Webseiten, in Outlook oder Teams.
- **Automatischer Start mit Windows** (optional minimiert), damit die Konten nach der Anmeldung sofort erreichbar sind.
- **Mini-Ansicht** (Schaltfläche „Mini“ oben rechts oder `Strg+M`): nur Wählfeld und laufende Gespräche in einem
  schmalen Fenster, ohne Konten und Anrufliste. Die gewählte Ansicht bleibt beim nächsten Start erhalten.
- **Anrufer-Erkennung** über einen eigenen REST-Dienst (z. B. CRM oder Telefonbuch): Der Name des Anrufers wird
  angezeigt und in der Anrufliste gespeichert.

## Anrufer-Erkennung (REST-Dienst)

In **Einstellungen → Anrufer-Erkennung** die Service-URL und optional einen API-Key eintragen. Mit **Testen** kann
der Dienst direkt mit einer Rufnummer ausprobiert werden. Bei jedem eingehenden Anruf mit Rufnummer ruft rbs den
Dienst per `GET` auf:

- **Rufnummer:** Der Platzhalter `{number}` wird durch die Rufnummer ersetzt (URL-kodiert, so wie sie vom Anbieter
  kommt, z. B. `+49721123456` oder `0721123456`), z. B. `https://crm.example.de/api/caller?phone={number}`.
  Ohne Platzhalter wird `?number=…` angehängt.
- **API-Key:** wird im Header `X-API-Key` mitgeschickt. Erwartet der Dienst ihn in der URL, kann `{apikey}` als
  Platzhalter verwendet werden. Der Key wird mit Windows-DPAPI verschlüsselt gespeichert und nie protokolliert.
- **Antwort:** der Name als reiner Text (`Max Mustermann`) oder als JSON, entweder als Zeichenkette oder als Objekt
  mit einem der Felder `name`, `displayName`, `callerName`, `fullName`, `company` (auch `firstName` + `lastName`,
  auch eingebettet in `data` oder `result`). `404`, `204` oder eine leere Antwort bedeuten „unbekannt“.
- **Verhalten:** Der Dienst muss innerhalb von 4 Sekunden antworten. Die Anzeige erscheint sofort mit der Nummer bzw.
  dem vom Anbieter übermittelten Namen; der gefundene Name ersetzt diesen. Ergebnisse werden 10 Minuten
  zwischengespeichert. Weiterleitungen (HTTP 3xx) werden nicht verfolgt, damit der API-Key nicht an andere Server geht.
  Bei Fehlern (Dienst nicht erreichbar, Zeitüberschreitung) bleibt die Anzeige wie bisher.
- Bitte `https` verwenden, sonst wird der API-Key unverschlüsselt übertragen (der Test weist darauf hin).

## Automatisch mit Windows starten

In **Einstellungen → Verhalten** „Automatisch beim Windows-Start starten“ ankreuzen (optional „Dabei minimiert starten“)
und speichern. rbs trägt sich dann für den aktuellen Benutzer in den Windows-Autostart ein
(`HKCU\…\CurrentVersion\Run`, keine Admin-Rechte nötig). Der Eintrag erscheint auch unter
**Windows-Einstellungen → Apps → Autostart** bzw. im Task-Manager und kann dort ebenfalls abgeschaltet werden.
Die App erkennt das und zeigt den Autostart dann als aus an.

- Läuft rbs beim Windows-Start bereits, passiert nichts weiter.
- Wird die Programmdatei verschoben oder per ClickOnce aktualisiert, trägt rbs den neuen Pfad beim nächsten
  manuellen Start automatisch ein.
- Vor einer Deinstallation den Autostart abschalten, da ClickOnce den Eintrag nicht selbst entfernt.

## Als Standard-App für tel:-Links verwenden

1. In rbs **Einstellungen → Telefon-Links → „Bei Windows anmelden“** klicken
   (alternativ `rbs-sip-fone.exe --register`). Es werden nur Einträge für den aktuellen Benutzer angelegt (HKCU), keine Admin-Rechte nötig.
2. Windows öffnet die Einstellungen „Standard-Apps“ bei ringring best sipfon (rbs). Dort für **TEL**, **CALLTO** und ggf. **SIP**
   rbs auswählen. Diesen Schritt lässt Windows bewusst nur den Benutzer selbst ausführen.

Verhalten beim Klick auf einen Link:

- Läuft rbs noch nicht, startet es und übernimmt die Nummer; läuft es bereits, wird der Link an das
  laufende Programm übergeben (Named Pipe, nur für den eigenen Windows-Benutzer) und das Fenster nach vorne geholt.
- Standardmäßig wird die Nummer nur ins Wählfeld übernommen. Mit „Bei Klick auf einen Link sofort wählen“ wird direkt
  über das ausgewählte Konto angerufen.
- Rufnummern werden bereinigt: `tel:+49 (0)721 / 12 34-56` → `+49721123456`; `;phone-context=…` u. Ä. wird ignoriert.
- Wird die Programmdatei verschoben oder per ClickOnce aktualisiert (neuer Ordner), aktualisiert rbs die
  Anmeldung beim nächsten Start automatisch. Vor einer Deinstallation „Anmeldung entfernen“ klicken
  (oder `rbs-sip-fone.exe --unregister`), da ClickOnce diese Einträge nicht selbst entfernt.

## Bauen und starten

Voraussetzung: .NET 10 SDK, Windows 10 (1809) oder neuer.

```powershell
cd src\RbsSipFone
dotnet run
```

Eigenständige Weitergabe (eine Exe, ohne installiertes .NET):

```powershell
dotnet publish src\RbsSipFone -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Bei jeder Weitergabe der Programmdateien (z. B. als GitHub-Release oder ClickOnce-Installation) müssen
[LICENSE](LICENSE) und [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) beigelegt werden.

## Daten

Alles liegt unter `%APPDATA%\rbs-sip-fone`:

| Datei | Inhalt |
| --- | --- |
| `settings.json` | Konten und Einstellungen. Passwörter sind mit Windows-DPAPI benutzergebunden verschlüsselt. |
| `history.json` | Anrufliste (max. 200 Einträge) |
| `sip.log` | Protokoll; mit „SIP-Nachrichten protokollieren“ inkl. kompletter SIP-Nachrichten |

Mit der Umgebungsvariablen `RBS_SIP_FONE_HOME` kann ein anderer Ordner verwendet werden (z. B. für Tests oder portablen Betrieb).

## Hinweise und Grenzen

- Sprachdaten (RTP) werden unverschlüsselt übertragen; TLS sichert nur die Signalisierung (kein SRTP).
- Kein STUN/ICE: Hinter NAT funktioniert das in der Regel mit Anbietern, die symmetrisches RTP/`rport` nutzen
  (z. B. sipgate, Telekom, FRITZ!Box im LAN). Bei Problemen mit einseitigem Ton hilft ein Outbound-Proxy bzw. die Telefonanlage.
- Codecs (Reihenfolge im Angebot): PCMU (G.711 µ), PCMA (G.711 A), G.722 (HD), G.729.
- Der lokale SIP-Port (Standard 5060) wird automatisch gewechselt, wenn er belegt ist.
- Es kann nur eine Instanz gleichzeitig laufen.
- Nicht für Notrufe geeignet (siehe Hinweis oben).

## Projektaufbau

```
src/RbsSipFone/
  Sip/SipEngine.cs              gemeinsamer SIP-Transport (UDP/TCP/TLS), Verteilung eingehender Anfragen
  ViewModels/AccountViewModel   Konto + Registrierung (SIPRegistrationUserAgent)
  ViewModels/CallViewModel      ein Gespräch (SIPUserAgent + WindowsAudioEndPoint)
  ViewModels/MainViewModel      Konten, Gespräche, Wählfeld, Anrufliste, Klingel-/Freiton-Logik
  Services/                     Konfiguration, Protokoll, Audiogeräte, Tonerzeugung,
                                Link-Parser (PhoneLink), Windows-Registrierung, Autostart, Anrufer-Erkennung (CallerLookup), Einzelinstanz (SingleInstance)
  Themes/Theme.xaml             rbs-Gestaltung (Farben, Buttons, Felder, Schalter, Scrollbars)
  Views/                        Hauptfenster und Dialoge
tests/RbsSipFone.Tests/         Unit-Tests (xUnit), z. B. für den Link-Parser – ausführen mit `dotnet test`
```

## Lizenz

**Quellcode:** Der Quellcode von rbs steht unter der [MIT-Lizenz](LICENSE). Er wird ohne Gewähr
bereitgestellt.

**Logo und Marke ausgenommen:** Name und Logo „rbs Redaktionsbüro Stutensee“, die Bilddateien in
`src/RbsSipFone/Assets/` (`rbs-logo.png`, `rbs-symbol.png`, `app.ico`) und die rbs-Farbgestaltung fallen **nicht**
unter die MIT-Lizenz. Alle Rechte daran sind vorbehalten. Wer das Projekt weiterentwickelt oder eigene Versionen
verteilt, muss Logo und Namen durch eigene ersetzen. Das Projekt darf nicht so auftreten, als stamme es vom
rbs Redaktionsbüro Stutensee.

**Drittanbieter-Komponenten:** Die verwendeten Bibliotheken stehen unter eigenen Lizenzen (MIT, BSD, Apache 2.0),
aufgeführt in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Die Lizenz von **SIPSorcery** enthält zusätzlich zur BSD-3-Clause-Lizenz eine Nutzungsbeschränkung des
SIPSorcery-Rechteinhabers. Danach sind Nutzung, Veränderung und Weitergabe innerhalb Israels und der besetzten
Gebiete nicht gestattet. Diese Beschränkung gilt für alle Programmdateien, die SIPSorcery enthalten. Deshalb
erfüllt das Gesamtpaket nicht die Open Source Definition der OSI, auch wenn der eigene Code unter MIT steht.
