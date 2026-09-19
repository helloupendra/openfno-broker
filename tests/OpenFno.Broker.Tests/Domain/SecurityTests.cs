using System.Text;
using OpenFno.Broker.Domain.Security;

namespace OpenFno.Broker.Tests.Domain;

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1 key; the RFC lists 8 digits, the last 6 are the 6-digit code.
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Matches_the_rfc_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, Totp.Code(RfcKey, step));
    }

    [Fact]
    public void Accepts_one_step_of_drift_either_way_and_nothing_further()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Assert.Equal(step - 1, Totp.Verify(RfcKey, Totp.Code(RfcKey, step - 1), now, lastUsedStep: 0));
        Assert.Equal(step + 1, Totp.Verify(RfcKey, Totp.Code(RfcKey, step + 1), now, lastUsedStep: 0));
        Assert.Null(Totp.Verify(RfcKey, Totp.Code(RfcKey, step - 2), now, lastUsedStep: 0));
        Assert.Null(Totp.Verify(RfcKey, Totp.Code(RfcKey, step + 2), now, lastUsedStep: 0));
    }

    [Fact]
    public void A_code_cannot_be_used_twice()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var code = Totp.Code(RfcKey, Totp.StepAt(now));

        var used = Totp.Verify(RfcKey, code, now, lastUsedStep: 0);
        Assert.NotNull(used);
        Assert.Null(Totp.Verify(RfcKey, code, now, lastUsedStep: used!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12345a")]
    [InlineData("1234567")]
    public void Refuses_malformed_codes(string? code)
        => Assert.Null(Totp.Verify(RfcKey, code, DateTimeOffset.UnixEpoch.AddYears(50), 0));

    [Fact]
    public void Provisioning_uri_carries_the_secret_for_authenticator_apps()
    {
        var uri = Totp.ProvisioningUri(RfcKey, "OFB00001", "OpenFNO Broker");
        Assert.StartsWith("otpauth://totp/OpenFNO%20Broker:OFB00001?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("period=30", uri);
    }
}

public class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encodes_the_rfc_4648_vectors_without_padding(string text, string expected)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, Base32.Encode(bytes));
        Assert.Equal(bytes, Base32.Decode(expected));
    }

    [Fact]
    public void Decodes_lower_case_padding_and_spaces()
        => Assert.Equal(Encoding.ASCII.GetBytes("foobar"), Base32.Decode("mzxw 6ytb oi======"));

    [Fact]
    public void Rejects_characters_outside_the_alphabet()
        => Assert.Throws<FormatException>(() => Base32.Decode("MZXW1"));
}
