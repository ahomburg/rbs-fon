using System.Text.RegularExpressions;

namespace RbsSipFone.Services;

/// <summary>Wandelt Links wie tel:, callto:, sip: und sips: in eine wählbare Rufnummer bzw. SIP-Adresse um.</summary>
public static partial class PhoneLink
{
    public static readonly string[] Schemes = { "tel", "callto", "sip", "sips" };

    private const int MaxLength = 512;

    /// <summary>Liefert die zu wählende Zeichenkette oder null, wenn der Link nicht verwendbar ist.</summary>
    public static string? ToDialString(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;
        var text = link.Trim().Trim('"').Trim();
        if (text.Length == 0 || text.Length > MaxLength) return null;

        var colon = text.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = text[..colon].ToLowerInvariant();
        var rest = text[(colon + 1)..].TrimStart('/');

        return scheme switch
        {
            "tel" => PhoneNumber(rest),
            "callto" => rest.Contains('@') ? SipAddress("sip", rest) : PhoneNumber(rest),
            "sip" or "sips" => SipAddress(scheme, rest),
            _ => null
        };
    }

    private static string? PhoneNumber(string value)
    {
        // Parameter (;phone-context=…, ;ext=…) und Query abschneiden.
        var end = value.IndexOfAny(new[] { ';', '?' });
        if (end >= 0) value = value[..end];
        value = Decode(value).Trim();

        // "+49 (0) 721 …": die in Klammern notierte Verkehrsausscheidungsziffer entfällt bei internationaler Schreibweise.
        if (value.StartsWith('+')) value = TrunkPrefix().Replace(value, "");

        var number = VisualSeparators().Replace(value, "");
        return ValidNumber().IsMatch(number) ? number : null;
    }

    private static string? SipAddress(string scheme, string value)
    {
        var end = value.IndexOfAny(new[] { ';', '?' });
        if (end >= 0) value = value[..end];
        value = Decode(value).Trim();
        return ValidSipAddress().IsMatch(value) ? $"{scheme}:{value}" : null;
    }

    private static string Decode(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
    }

    [GeneratedRegex(@"\(\s*0\s*\)")]
    private static partial Regex TrunkPrefix();

    [GeneratedRegex(@"[\s\-\.\(\)/]")]
    private static partial Regex VisualSeparators();

    [GeneratedRegex(@"^\+?[0-9*#]{1,32}$")]
    private static partial Regex ValidNumber();

    [GeneratedRegex(@"^[A-Za-z0-9\-_.!~*'()&=+$,;?/%]+@[A-Za-z0-9\-.\[\]:]+$|^[A-Za-z0-9\-.]+(:\d+)?$")]
    private static partial Regex ValidSipAddress();
}
