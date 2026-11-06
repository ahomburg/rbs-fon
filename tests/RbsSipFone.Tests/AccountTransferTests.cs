using System.Text.Json.Nodes;
using RbsSipFone.Models;
using RbsSipFone.Services;
using Xunit;

namespace RbsSipFone.Tests;

public class AccountTransferTests
{
    private static AccountConfig Account(string name = "Büro", string user = "max", string domain = "sip.example.org", string password = "geheim-123")
    {
        var account = new AccountConfig
        {
            Name = name,
            DisplayName = "Max Muster",
            Username = user,
            AuthUsername = "auth-max",
            Domain = domain,
            Proxy = "proxy.example.org:5060",
            Transport = SipTransportKind.Tls,
            Expiry = 600
        };
        account.Password = password;
        return account;
    }

    [Fact]
    public void Round_trip_restores_all_fields_including_password()
    {
        var data = Assert.Single(AccountTransfer.Read(AccountTransfer.Export([Account()], includePasswords: true)));
        var config = AccountTransfer.ToConfig(data, null);

        Assert.Equal("Büro", config.Name);
        Assert.Equal("Max Muster", config.DisplayName);
        Assert.Equal("max", config.Username);
        Assert.Equal("auth-max", config.AuthUsername);
        Assert.Equal("sip.example.org", config.Domain);
        Assert.Equal("proxy.example.org:5060", config.Proxy);
        Assert.Equal(SipTransportKind.Tls, config.Transport);
        Assert.Equal(600, config.Expiry);
        Assert.True(config.Enabled);
        Assert.Equal("geheim-123", config.Password);
    }

    [Fact]
    public void Export_is_plain_readable_json()
    {
        var json = AccountTransfer.Export([Account(password: "pass\"wört")], includePasswords: true);
        var account = JsonNode.Parse(json)!["accounts"]![0]!;

        Assert.Contains("Büro", json); // Umlaute bleiben lesbar
        Assert.Equal("pass\"wört", account["password"]!.GetValue<string>());
        Assert.Equal("Tls", account["transport"]!.GetValue<string>());
        Assert.DoesNotContain("protectedPassword", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Export_without_passwords_contains_none_and_imports_disabled()
    {
        var json = AccountTransfer.Export([Account()], includePasswords: false);
        Assert.DoesNotContain("geheim-123", json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        var config = AccountTransfer.ToConfig(Assert.Single(AccountTransfer.Read(json)), null);
        Assert.Equal("", config.Password);
        Assert.False(config.Enabled);
    }

    [Fact]
    public void Hand_written_minimal_file_uses_defaults()
    {
        var data = Assert.Single(AccountTransfer.Read("""
            { "accounts": [ { "username": "max", "domain": "sip.example.org", "password": "geheim" } ] }
            """));
        var config = AccountTransfer.ToConfig(data, null);

        Assert.Equal("max@sip.example.org", config.AddressOfRecord);
        Assert.Equal("geheim", config.Password);
        Assert.Equal(SipTransportKind.Udp, config.Transport);
        Assert.Equal(300, config.Expiry);
        Assert.True(config.Enabled);
        Assert.Null(Assert.Single(AccountTransfer.Prepare([data], [])).Error);
    }

    [Fact]
    public void Hand_written_file_may_have_comments_trailing_commas_and_any_case()
    {
        var data = AccountTransfer.Read("""
            {
              // Büro-Anschluss
              "Accounts": [
                {
                  "Username": "max",
                  "DOMAIN": "sip.example.org",
                  "transport": "tls",
                  "password": "geheim",
                },
              ],
            }
            """);

        Assert.Equal(SipTransportKind.Tls, Assert.Single(data).Transport);
    }

    [Theory]
    [InlineData("{ \"accounts\": [ { \"username\": null, \"domain\": null, \"name\": null, \"proxy\": null } ] }")]
    [InlineData("{ \"accounts\": [ { } ] }")]
    public void Missing_or_null_address_is_flagged_not_a_crash(string json)
    {
        var candidate = Assert.Single(AccountTransfer.Prepare(AccountTransfer.Read(json), []));
        Assert.NotNull(candidate.Error);
    }

    [Fact]
    public void Overwriting_keeps_id_and_password_when_file_has_none()
    {
        var existing = Account(password: "bleibt");
        var data = Assert.Single(AccountTransfer.Read(AccountTransfer.Export([Account(name: "Neu")], includePasswords: false)));

        var config = AccountTransfer.ToConfig(data, existing);

        Assert.Equal(existing.Id, config.Id);
        Assert.Equal("Neu", config.Name);
        Assert.Equal("bleibt", config.Password);
        Assert.True(config.Enabled);
    }

    [Fact]
    public void Overwriting_with_password_replaces_it_and_new_accounts_get_new_id()
    {
        var existing = Account(password: "alt");
        var data = Assert.Single(AccountTransfer.Read(AccountTransfer.Export([Account(password: "neu")], includePasswords: true)));

        Assert.Equal("neu", AccountTransfer.ToConfig(data, existing).Password);
        Assert.NotEqual(existing.Id, AccountTransfer.ToConfig(data, null).Id);
    }

    [Fact]
    public void Prepare_matches_each_existing_account_once_and_prefers_same_name()
    {
        var first = Account(name: "A");
        var second = Account(name: "B");
        var data = AccountTransfer.Read(AccountTransfer.Export(
            [Account(name: "B"), Account(name: "A"), Account(name: "C"), Account(name: "D", user: "neu")], includePasswords: false));

        var result = AccountTransfer.Prepare(data, [first, second]);

        Assert.Same(second, result[0].Existing);
        Assert.Same(first, result[1].Existing);
        Assert.Null(result[2].Existing); // beide vorhandenen sind schon vergeben
        Assert.Null(result[3].Existing);
    }

    [Theory]
    [InlineData("max\r\nX-Evil: 1", "sip.example.org")]
    [InlineData("max", "sip.example.org\r\nX-Evil: 1")]
    [InlineData("max@evil", "sip.example.org")]
    [InlineData("", "sip.example.org")]
    [InlineData("max", "")]
    public void Prepare_flags_invalid_addresses(string user, string domain)
    {
        var data = new AccountData { Username = user, Domain = domain };
        Assert.NotNull(Assert.Single(AccountTransfer.Prepare([data], [])).Error);
    }

    [Fact]
    public void Prepare_flags_control_characters_in_proxy()
    {
        var data = new AccountData { Username = "max", Domain = "sip.example.org", Proxy = "p\r\nX: 1" };
        Assert.NotNull(Assert.Single(AccountTransfer.Prepare([data], [])).Error);
    }

    [Fact]
    public void ToConfig_clamps_expiry_and_strips_control_characters_from_names()
    {
        var config = AccountTransfer.ToConfig(new AccountData
        {
            Name = "Bü\r\nro",
            DisplayName = "Max\u0007",
            Username = "max",
            Domain = "sip.example.org",
            Expiry = 5
        }, null);

        Assert.Equal("Büro", config.Name);
        Assert.Equal("Max", config.DisplayName);
        Assert.Equal(60, config.Expiry);
    }

    [Theory]
    [InlineData("kein json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"accounts\":[null]}")]
    [InlineData("{\"format\":\"etwas-anderes\",\"version\":1,\"accounts\":[]}")]
    [InlineData("{\"version\":99,\"accounts\":[]}")]
    [InlineData("{\"accounts\":[{\"username\":\"a\",\"domain\":\"b\",\"transport\":\"Brieftaube\"}]}")]
    [InlineData("{\"accounts\":[{\"username\":\"a\",\"domain\":\"b\",\"transport\":7}]}")]
    public void Invalid_files_are_rejected(string json) =>
        Assert.Throws<AccountFileException>(() => AccountTransfer.Read(json));

    [Fact]
    public void Syntax_error_message_names_the_line()
    {
        var ex = Assert.Throws<AccountFileException>(() => AccountTransfer.Read("{\n  \"accounts\": [\n    { \"username\" \"max\" }\n  ]\n}"));
        Assert.Contains("Zeile 3", ex.Message);
    }

    [Fact]
    public void Too_many_accounts_are_rejected()
    {
        var json = AccountTransfer.Export(Enumerable.Range(0, 201).Select(i => new AccountConfig { Username = "u" + i, Domain = "d" }), includePasswords: false);
        Assert.Throws<AccountFileException>(() => AccountTransfer.Read(json));
    }
}
