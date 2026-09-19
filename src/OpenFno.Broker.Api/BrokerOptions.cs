namespace OpenFno.Broker.Api;

public enum StorageKind
{
    /// <summary>Everything in memory; gone when the process stops. For tests and a first look.</summary>
    Memory,

    /// <summary>Journal and request log in Postgres.</summary>
    Postgres,
}

public sealed class BrokerOptions
{
    public const string Section = "Broker";

    public StorageKind Storage { get; set; } = StorageKind.Memory;

    public string? ConnectionString { get; set; }

    /// <summary>The key the <c>X-Admin-Key</c> header must carry. Unset, the admin API is off.</summary>
    public string? AdminKey { get; set; }

    /// <summary>Holds calendar/, reference/, instruments/ and keys/. Relative paths start at the content root.</summary>
    public string DataDirectory { get; set; } = "../../data";

    /// <summary>
    /// A header carrying the caller's real IP, set by a reverse proxy in front
    /// of the broker (<c>CF-Connecting-IP</c> behind a Cloudflare tunnel). It is
    /// believed only when the connection itself comes from this machine.
    /// </summary>
    public string? ClientIpHeader { get; set; }

    /// <summary>How often the market clock expires orders, squares off intraday positions and settles the day.</summary>
    public int ClockSweepSeconds { get; set; } = 5;
}
