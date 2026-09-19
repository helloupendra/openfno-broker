using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Domain.Instruments;

namespace OpenFno.Broker.Application.Market;

/// <summary>An immutable lookup of instruments by symbol (case-insensitive).</summary>
public sealed class InstrumentCatalog : IInstrumentCatalog
{
    private readonly Dictionary<string, Instrument> _bySymbol;

    public InstrumentCatalog(IEnumerable<Instrument> instruments)
    {
        _bySymbol = new Dictionary<string, Instrument>(StringComparer.OrdinalIgnoreCase);
        foreach (var instrument in instruments) _bySymbol[instrument.Symbol] = instrument;
    }

    public int Count => _bySymbol.Count;

    public Instrument? Find(string symbol) => _bySymbol.GetValueOrDefault(symbol.Trim());
}
