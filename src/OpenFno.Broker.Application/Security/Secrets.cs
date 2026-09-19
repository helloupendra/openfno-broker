using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenFno.Broker.Application.Security;

public static class Secrets
{
    private const string IdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>A random URL-safe token with 256 bits of entropy.</summary>
    public static string NewToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>A short random identifier from an alphabet without look-alike characters.</summary>
    public static string NewId(string prefix, int length = 10)
        => prefix + RandomNumberGenerator.GetString(IdAlphabet, length);

    /// <summary>
    /// SHA-256, hex. Enough for tokens and app secrets, which are random and
    /// 256 bits long; a slow password hash is for secrets people choose.
    /// </summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool HashMatches(string secret, string expectedHash)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Hash(secret)),
            Encoding.ASCII.GetBytes(expectedHash));

    /// <summary>An IP address in canonical text, with IPv4-mapped IPv6 turned back into IPv4; null when it is not an address.</summary>
    public static string? NormalizeIp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IPAddress.TryParse(text.Trim(), out var address)) return null;
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.ToString();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
