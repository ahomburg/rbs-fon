using RbsSipFone.Services;
using Xunit;

namespace RbsSipFone.Tests;

public class CallerLookupTests
{
    [Theory]
    [InlineData("https://crm.example.de/api/caller?phone={number}", "+49721123", "https://crm.example.de/api/caller?phone=%2B49721123")]
    [InlineData("https://crm.example.de/caller/{number}", "0721 55", "https://crm.example.de/caller/0721%2055")]
    [InlineData("https://crm.example.de/lookup", "0721", "https://crm.example.de/lookup?number=0721")]
    [InlineData("https://crm.example.de/lookup?x=1", "0721", "https://crm.example.de/lookup?x=1&number=0721")]
    [InlineData("https://crm.example.de/lookup?", "0721", "https://crm.example.de/lookup?number=0721")]
    [InlineData("https://crm.example.de/lookup?n={NUMBER}&key={apikey}", "0721", "https://crm.example.de/lookup?n=0721&key=k%26y")]
    public void BuildUri_inserts_number_and_key(string template, string number, string expected)
    {
        Assert.Equal(expected, CallerLookup.BuildUri(template, number, "k&y").AbsoluteUri);
    }

    [Theory]
    [InlineData("Max Mustermann", "text/plain", "Max Mustermann")]
    [InlineData("  Max Mustermann \r\nzweite Zeile", "text/plain", "Max Mustermann")]
    [InlineData("\"Max Mustermann\"", "application/json", "Max Mustermann")]
    [InlineData("{\"name\":\"Max Mustermann\"}", "application/json", "Max Mustermann")]
    [InlineData("{\"Name\":\"Erika\",\"company\":\"ACME\"}", null, "Erika")]
    [InlineData("{\"displayName\":\"Erika M.\"}", "application/json", "Erika M.")]
    [InlineData("{\"firstName\":\"Erika\",\"lastName\":\"Muster\"}", "application/json", "Erika Muster")]
    [InlineData("{\"data\":{\"callerName\":\"Firma XY\"}}", "application/json", "Firma XY")]
    [InlineData("[{\"name\":\"Erster\"},{\"name\":\"Zweiter\"}]", "application/json", "Erster")]
    [InlineData("{\"company\":\"ACME GmbH\"}", "application/json", "ACME GmbH")]
    public void ParseName_reads_text_and_json(string body, string? mediaType, string expected)
    {
        Assert.Equal(expected, CallerLookup.ParseName(body, mediaType));
    }

    [Theory]
    [InlineData("", "text/plain")]
    [InlineData("   ", null)]
    [InlineData("null", "application/json")]
    [InlineData("{}", "application/json")]
    [InlineData("{\"name\":null}", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("<html><body>Login</body></html>", "text/html")]
    [InlineData("{kaputt", "application/json")]
    public void ParseName_returns_null_without_name(string body, string? mediaType)
    {
        Assert.Null(CallerLookup.ParseName(body, mediaType));
    }

    [Fact]
    public void ParseName_limits_length_and_strips_control_characters()
    {
        var name = CallerLookup.ParseName("{\"name\":\"A\\u0007B" + new string('x', 200) + "\"}", "application/json");
        Assert.NotNull(name);
        Assert.StartsWith("A B", name);
        Assert.True(name!.Length <= 81);
    }

    [Theory]
    [InlineData("+49721123", true)]
    [InlineData("0721", true)]
    [InlineData("Unterdrückt", false)]
    [InlineData("anonymous", false)]
    [InlineData("", false)]
    public void IsLookupable(string number, bool expected) => Assert.Equal(expected, CallerLookup.IsLookupable(number));

    [Theory]
    [InlineData("https://crm.example.de/{number}", true)]
    [InlineData("http://localhost:8080/caller?n={number}", true)]
    [InlineData("crm.example.de/caller", false)]
    [InlineData("ftp://crm.example.de/{number}", false)]
    [InlineData("", true)]
    public void Validate(string url, bool valid) => Assert.Equal(valid, CallerLookup.Validate(url) == null);

    [Theory]
    [InlineData("http://crm.example.de/{number}", "key", true)]
    [InlineData("http://localhost/{number}", "key", false)]
    [InlineData("https://crm.example.de/{number}", "key", false)]
    [InlineData("http://crm.example.de/{number}", "", false)]
    public void SendsKeyInsecurely(string url, string key, bool expected) =>
        Assert.Equal(expected, CallerLookup.SendsKeyInsecurely(url, key));
}
