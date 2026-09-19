using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OpenFno.Broker.Domain.Security;

/// <summary>
/// Time-based one-time passwords (RFC 6238: HMAC-SHA1, 30-second steps, six
/// digits), the second factor Indian brokers ask for at the daily API login.
/// Any authenticator app can hold the secret.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = (BinaryPrimitives.ReadInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFF_FFFF);
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString().PadLeft(digits, '0');
    }

    /// <summary>
    /// The step a code belongs to, if it is valid now (one step of clock drift
    /// either way is accepted) and newer than <paramref name="lastUsedStep"/>:
    /// a code, once used, cannot log in again.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;

        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step <= lastUsedStep) continue;
            var expected = Code(secret, step);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected),
                    System.Text.Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    /// <summary>The otpauth:// URI an authenticator app scans to add the account.</summary>
    public static string ProvisioningUri(byte[] secret, string account, string issuer)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
           + $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
}
