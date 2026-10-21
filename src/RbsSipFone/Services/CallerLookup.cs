using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace RbsSipFone.Services;

/// <summary>Ergebnis einer Anrufer-Suche: gefundener Name (oder null) und eine kurze Beschreibung für die Anzeige.</summary>
public sealed record CallerLookupResult(string? Name, string Info);

/// <summary>
/// Ermittelt den Namen eines Anrufers über einen REST-Dienst (HTTP GET).
/// Die Rufnummer wird über den Platzhalter {number} in die URL eingesetzt (sonst als Parameter „number“ angehängt),
/// der API-Key als Header „X-API-Key“ mitgeschickt bzw. über den Platzhalter {apikey} eingesetzt.
/// Antwort: reiner Text oder JSON (Zeichenkette oder Objekt mit z. B. „name“).
/// </summary>
public sealed class CallerLookup
{
    public const string NumberPlaceholder = "{number}";
    public const string ApiKeyPlaceholder = "{apikey}";
    public const string ApiKeyHeader = "X-API-Key";
    private const int MaxNameLength = 80;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(10);

    /// <summary>Feldnamen, unter denen der Name in einer JSON-Antwort gesucht wird (Reihenfolge = Priorität).</summary>
    private static readonly string[] NameFields =
        ["name", "displayName", "display_name", "callerName", "caller_name", "fullName", "full_name", "caller", "company"];

    private static readonly string[] WrapperFields = ["data", "result", "contact", "caller"];

    private static readonly HttpClient Http = CreateClient();

    private readonly ConcurrentDictionary<string, (CallerLookupResult Result, DateTime At)> _cache = new();

    public static bool IsConfigured(string? url) => !string.IsNullOrWhiteSpace(url);

    /// <summary>Lohnt sich eine Suche für diese Nummer (Ziffern vorhanden, nicht unterdrückt)?</summary>
    public static bool IsLookupable(string? number) =>
        !string.IsNullOrWhiteSpace(number) && number.Length <= 64 && number.Any(char.IsDigit);

    /// <summary>Prüft die Service-URL; liefert eine Fehlermeldung oder null.</summary>
    public static string? Validate(string? url)
    {
        if (!IsConfigured(url)) return null;
        var probe = url!.Trim().Replace(NumberPlaceholder, "0", StringComparison.OrdinalIgnoreCase)
                               .Replace(ApiKeyPlaceholder, "x", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "Bitte eine vollständige Service-URL mit http:// oder https:// angeben.";
        return null;
    }

    /// <summary>Wird der API-Key unverschlüsselt an einen Rechner außerhalb dieses PCs geschickt?</summary>
    public static bool SendsKeyInsecurely(string? url, string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || !IsConfigured(url)) return false;
        var probe = url!.Trim().Replace(NumberPlaceholder, "0", StringComparison.OrdinalIgnoreCase)
                               .Replace(ApiKeyPlaceholder, "x", StringComparison.OrdinalIgnoreCase);
        return Uri.TryCreate(probe, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback;
    }

    /// <summary>Setzt Rufnummer und ggf. API-Key in die URL ein.</summary>
    public static Uri BuildUri(string template, string number, string? apiKey)
    {
        var url = template.Trim();
        var encodedNumber = Uri.EscapeDataString(number);
        if (url.Contains(NumberPlaceholder, StringComparison.OrdinalIgnoreCase))
            url = url.Replace(NumberPlaceholder, encodedNumber, StringComparison.OrdinalIgnoreCase);
        else
            url += (url.Contains('?') ? (url.EndsWith('?') || url.EndsWith('&') ? "" : "&") : "?") + "number=" + encodedNumber;
        url = url.Replace(ApiKeyPlaceholder, Uri.EscapeDataString(apiKey ?? ""), StringComparison.OrdinalIgnoreCase);
        return new Uri(url, UriKind.Absolute);
    }

    /// <summary>Liest den Namen aus der Antwort (Text oder JSON); null, wenn keiner enthalten ist.</summary>
    public static string? ParseName(string? body, string? mediaType)
    {
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true || text.StartsWith('<')) return null;

        var looksJson = mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true || text[0] is '{' or '[' or '"';
        if (looksJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return Clean(FromJson(doc.RootElement, depth: 0));
            }
            catch (JsonException)
            {
                if (mediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true) return null;
            }
        }

        var firstLine = text.Split('\n', 2)[0];
        return Clean(firstLine);
    }

    private static string? FromJson(JsonElement e, int depth)
    {
        if (depth > 3) return null;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) return FromJson(item, depth + 1);
                return null;
            case JsonValueKind.Object:
                foreach (var field in NameFields)
                {
                    var value = Property(e, field);
                    if (value is { ValueKind: JsonValueKind.String } s && !string.IsNullOrWhiteSpace(s.GetString())) return s.GetString();
                }
                // Vor- und Nachname getrennt geliefert?
                var first = Property(e, "firstName") ?? Property(e, "first_name");
                var last = Property(e, "lastName") ?? Property(e, "last_name");
                var combined = $"{(first?.ValueKind == JsonValueKind.String ? first.Value.GetString() : "")} {(last?.ValueKind == JsonValueKind.String ? last.Value.GetString() : "")}".Trim();
                if (combined.Length > 0) return combined;
                foreach (var field in WrapperFields)
                {
                    if (Property(e, field) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } inner &&
                        FromJson(inner, depth + 1) is { } found) return found;
                }
                return null;
            default:
                return null;
        }
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        var result = sb.ToString().Trim();
        if (result.Length == 0 || result.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        return result.Length > MaxNameLength ? result[..MaxNameLength].TrimEnd() + "…" : result;
    }

    /// <summary>Sucht den Namen zur Rufnummer. Ergebnisse werden 10 Minuten zwischengespeichert.</summary>
    public async Task<CallerLookupResult> LookupAsync(string urlTemplate, string? apiKey, string number,
        bool useCache = true, CancellationToken ct = default)
    {
        if (useCache && _cache.TryGetValue(number, out var cached) && DateTime.UtcNow - cached.At < CacheDuration)
            return cached.Result;

        var result = await QueryAsync(urlTemplate, apiKey, number, ct);
        // Fehler (Timeout, Serverfehler) nicht zwischenspeichern, damit der nächste Anruf es erneut versucht.
        if (result.Name != null || result.Info.StartsWith("Kein", StringComparison.Ordinal))
            _cache[number] = (result, DateTime.UtcNow);
        return result;
    }

    public void ClearCache() => _cache.Clear();

    private static async Task<CallerLookupResult> QueryAsync(string urlTemplate, string? apiKey, string number, CancellationToken ct)
    {
        Uri uri;
        try { uri = BuildUri(urlTemplate, number, apiKey); }
        catch (Exception ex) { return new(null, "Ungültige Service-URL: " + ex.Message); }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/plain;q=0.9");
            if (!string.IsNullOrEmpty(apiKey)) request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = $"HTTP {(int)response.StatusCode}";
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
                return Log(uri, number, new(null, $"Kein Eintrag gefunden ({status})"));
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Log(uri, number, new(null, $"Zugriff verweigert – bitte den API-Key prüfen ({status})"));
            if (!response.IsSuccessStatusCode)
                return Log(uri, number, new(null, $"Dienst meldet Fehler: {status} {response.ReasonPhrase}"));

            var body = await ReadLimitedAsync(response, ct);
            var name = ParseName(body, response.Content.Headers.ContentType?.MediaType);
            return Log(uri, number, name != null ? new(name, $"Gefunden ({status})") : new(null, $"Kein Name in der Antwort ({status})"));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Log(uri, number, new(null, "Zeitüberschreitung – der Dienst hat nicht rechtzeitig geantwortet."));
        }
        catch (HttpRequestException ex)
        {
            return Log(uri, number, new(null, "Dienst nicht erreichbar: " + ex.Message));
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new char[16 * 1024];
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return new string(buffer, 0, read);
    }

    private static CallerLookupResult Log(Uri uri, string number, CallerLookupResult result)
    {
        // Nur Host protokollieren – die URL kann den API-Key enthalten.
        SipLog.Write($"Anrufer-Suche {number} bei {uri.Host}: {result.Info}{(result.Name != null ? " → " + result.Name : "")}");
        return result;
    }

    private static HttpClient CreateClient()
    {
        // Keine Weiterleitungen folgen, damit der API-Key nicht an einen anderen Server geht.
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(4)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"rbs-sip-fone/{AppInfo.Version}");
        return client;
    }
}
