using System.Text.Json;
using OpenFno.Broker.Domain.Instruments;

namespace OpenFno.Broker.Infrastructure.Instruments;

public sealed record InstrumentLoadReport(
    IReadOnlyList<Instrument> Instruments,
    IReadOnlyList<string> FilesRead,
    IReadOnlyList<string> FilesMissing,
    int WithFreezeQuantity,
    int CommoditiesWithoutLotSize);

/// <summary>
/// Builds the instrument list from the masters in one folder: the FYERS masters
/// for symbols, lots, ticks and sessions; the Dhan master, when present, for
/// freeze quantities; and the commodity lot-size table for MCX lots.
/// </summary>
public static class InstrumentLoader
{
    public static InstrumentLoadReport Load(string directory, IReadOnlyDictionary<string, int> commodityLotSizes)
    {
        var read = new List<string>();
        var missing = new List<string>();

        Dictionary<(Exchange, string), ContractLimits> limits = [];
        var dhanPath = Path.Combine(directory, DhanScripMaster.FileName);
        if (File.Exists(dhanPath))
        {
            using var reader = File.OpenText(dhanPath);
            limits = DhanScripMaster.Parse(reader);
            read.Add(DhanScripMaster.FileName);
        }
        else
        {
            missing.Add(DhanScripMaster.FileName);
        }

        var instruments = new List<Instrument>();
        int withFreeze = 0, commodityWithoutLot = 0;
        foreach (var file in FyersSymbolMaster.Files)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                missing.Add(file);
                continue;
            }

            using var reader = File.OpenText(path);
            foreach (var parsed in FyersSymbolMaster.Parse(reader))
            {
                var instrument = Enrich(parsed, limits, commodityLotSizes);
                if (instrument.FreezeQuantity is not null) withFreeze++;
                if (instrument.Segment == Segment.Commodity && instrument.LotSize == 0) commodityWithoutLot++;
                instruments.Add(instrument);
            }
            read.Add(file);
        }

        return new InstrumentLoadReport(instruments, read, missing, withFreeze, commodityWithoutLot);
    }

    public static Instrument Enrich(
        Instrument instrument,
        IReadOnlyDictionary<(Exchange, string), ContractLimits> limits,
        IReadOnlyDictionary<string, int> commodityLotSizes)
    {
        var lotSize = instrument.LotSize;
        if (instrument.Segment == Segment.Commodity)
            lotSize = commodityLotSizes.TryGetValue(instrument.Underlying, out var configured) ? configured : 0;

        int? freeze = null;
        if (instrument.ExchangeToken is { } token
            && limits.TryGetValue((instrument.Exchange, token), out var contract)
            && contract.FreezeQuantity is { } raw)
        {
            // Commodity quantities in the Dhan master are in lots; everywhere else, in units.
            freeze = instrument.Segment == Segment.Commodity
                ? lotSize > 0 ? raw * lotSize : null
                : raw;
        }

        return instrument with { LotSize = lotSize, FreezeQuantity = freeze };
    }

    public static IReadOnlyDictionary<string, int> LoadCommodityLotSizes(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, int>();
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("lotSizes").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
    }
}
