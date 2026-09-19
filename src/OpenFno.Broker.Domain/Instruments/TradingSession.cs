using System.Globalization;

namespace OpenFno.Broker.Domain.Instruments;

/// <summary>The continuous trading hours of an instrument, in IST. Closing time is exclusive.</summary>
public readonly record struct TradingSession(TimeOnly Opens, TimeOnly Closes)
{
    public static readonly TradingSession NseCash = new(new TimeOnly(9, 15), new TimeOnly(15, 30));

    public bool Contains(TimeOnly time) => time >= Opens && time < Closes;

    /// <summary>
    /// Reads the session column of a FYERS symbol master, such as
    /// <c>0915-1530|1815-1915:</c>. The first range is the normal market; the
    /// ones after the bar are other windows and are not trading hours here.
    /// </summary>
    public static bool TryParse(string? text, out TradingSession session)
    {
        session = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var first = text.Split('|', 2)[0].Trim().TrimEnd(':');
        var parts = first.Split('-');
        if (parts.Length != 2) return false;
        if (!TryParseClock(parts[0], out var opens) || !TryParseClock(parts[1], out var closes)) return false;
        if (closes <= opens) return false;

        session = new TradingSession(opens, closes);
        return true;
    }

    private static bool TryParseClock(string text, out TimeOnly time)
        => TimeOnly.TryParseExact(text.Trim(), "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public override string ToString() => $"{Opens:HH\\:mm}-{Closes:HH\\:mm}";
}
