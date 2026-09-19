namespace OpenFno.Broker.Domain.Instruments;

public enum Exchange
{
    Nse,
    Bse,
    Mcx,
}

/// <summary>The exchange segment: cash market (CM), equity derivatives (FO) or commodities (COM).</summary>
public enum Segment
{
    Cash,
    Derivatives,
    Commodity,
}

public enum InstrumentKind
{
    Equity,
    Index,
    Future,
    Option,
}

public enum OptionRight
{
    Call,
    Put,
}

/// <summary>What a derivative is written on. Margin rates differ by it.</summary>
public enum UnderlyingClass
{
    None,
    Index,
    Stock,
    Commodity,
}
