using RbsSipFone.Services;
using Xunit;

namespace RbsSipFone.Tests;

public class PhoneLinkTests
{
    [Theory]
    [InlineData("tel:+49-721-123456", "+49721123456")]
    [InlineData("tel:+49%20(0)%20721%20123456", "+49721123456")]
    [InlineData("tel:+49 (0)721 / 12 34-56", "+49721123456")]
    [InlineData("tel:0721/123456;phone-context=example.com", "0721123456")]
    [InlineData("TEL:110", "110")]
    [InlineData("tel:*31#0721123", "*31#0721123")]
    [InlineData("\"tel:0721 123\"", "0721123")]
    [InlineData("callto://+49721123", "+49721123")]
    [InlineData("callto:0721%20123", "0721123")]
    [InlineData("callto:max@example.org", "sip:max@example.org")]
    [InlineData("sip:max@example.org", "sip:max@example.org")]
    [InlineData("sip:0721123@sipgate.de;user=phone", "sip:0721123@sipgate.de")]
    [InlineData("sips:max@example.org?subject=Hallo", "sips:max@example.org")]
    [InlineData("sip:max.mustermann@[2001:db8::1]:5061", "sip:max.mustermann@[2001:db8::1]:5061")]
    public void Accepts_valid_links(string link, string expected) =>
        Assert.Equal(expected, PhoneLink.ToDialString(link));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tel:")]
    [InlineData("tel:abc")]
    [InlineData("tel:0721+123")]
    [InlineData("http://example.org")]
    [InlineData("0721123456")]
    [InlineData("sip:")]
    [InlineData("sip:max @example.org")]
    [InlineData("sip:max@exa mple.org")]
    [InlineData("tel:012345678901234567890123456789012")]
    public void Rejects_invalid_links(string? link) =>
        Assert.Null(PhoneLink.ToDialString(link));
}
