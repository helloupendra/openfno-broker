using System.Globalization;
using System.Text;
using OpenFno.Broker.Domain.Instruments;

namespace OpenFno.Broker.Infrastructure.Instruments;

/// <summary>What the Dhan master adds to a FYERS row: the exchange's freeze quantity and, for commodities, the real lot size.</summary>
public sealed record ContractLimits(int LotSize, int? FreezeQuantity);

/// <summary>
/// Reads Dhan's detailed scrip master (https://images.dhan.co/api-data/api-scrip-master-detailed.csv),
/// which carries the exchange freeze quantity per contract and commodity lot
/// sizes, neither of which the FYERS masters have. Rows are keyed by exchange
/// and exchange token, which both vendors use as their contract id.
/// </summary>
public static class DhanScripMaster
{
    public const string FileName = "api-scrip-master-detailed.csv";

    public static Dictionary<(Exchange, string), ContractLimits> Parse(TextReader reader)
    {
        var result = new Dictionary<(Exchange, string), ContractLimits>();
        var header = reader.ReadLine();
        if (header is null) return result;

        var columns = SplitCsv(header);
        int Column(string name) => columns.FindIndex(c => string.Equals(c.Trim(), name, StringComparison.OrdinalIgnoreCase));
        int exchangeAt = Column("EXCH_ID"), tokenAt = Column("SECURITY_ID"), lotAt = Column("LOT_SIZE"), freezeAt = Column("SM_FREEZE_QTY");
        if (exchangeAt < 0 || tokenAt < 0 || lotAt < 0 || freezeAt < 0)
            throw new FormatException("The Dhan master lacks one of EXCH_ID, SECURITY_ID, LOT_SIZE, SM_FREEZE_QTY.");
        var width = new[] { exchangeAt, tokenAt, lotAt, freezeAt }.Max();

        while (reader.ReadLine() is { } line)
        {
            var f = SplitCsv(line);
            if (f.Count <= width) continue;

            Exchange? exchange = f[exchangeAt].Trim().ToUpperInvariant() switch
            {
                "NSE" => Exchange.Nse,
                "BSE" => Exchange.Bse,
                "MCX" => Exchange.Mcx,
                _ => null,
            };
            var token = f[tokenAt].Trim();
            if (exchange is null || token.Length == 0) continue;

            var lot = ParseInt(f[lotAt]);
            var freeze = ParseInt(f[freezeAt]);
            result[(exchange.Value, token)] = new ContractLimits(lot, freeze > 0 ? freeze : null);
        }
        return result;
    }

    private static int ParseInt(string text)
        => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0
            ? (int)decimal.Round(value)
            : 0;

    /// <summary>Splits one CSV line, honouring double-quoted fields.</summary>
    public static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
