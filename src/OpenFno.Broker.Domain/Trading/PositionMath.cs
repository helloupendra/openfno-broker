using OpenFno.Broker.Domain.Orders;

namespace OpenFno.Broker.Domain.Trading;

/// <summary>A position after a fill, and the profit or loss the fill booked.</summary>
public readonly record struct PositionChange(int Quantity, decimal AveragePrice, decimal Realised);

/// <summary>
/// Net-position arithmetic: a fill in the position's direction averages in; a
/// fill against it closes part or all of it and books the difference; a fill
/// larger than the position closes it and opens the other way at the fill price.
/// </summary>
public static class PositionMath
{
    public static PositionChange Apply(int quantity, decimal averagePrice, OrderSide side, int fillQuantity, decimal fillPrice)
    {
        if (fillQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(fillQuantity));
        var signed = side == OrderSide.Buy ? fillQuantity : -fillQuantity;

        if (quantity == 0 || Math.Sign(quantity) == Math.Sign(signed))
        {
            var total = Math.Abs(quantity) + fillQuantity;
            var average = (averagePrice * Math.Abs(quantity) + fillPrice * fillQuantity) / total;
            return new PositionChange(quantity + signed, Round(average, 4), 0m);
        }

        var closing = Math.Min(Math.Abs(quantity), fillQuantity);
        var perUnit = quantity > 0 ? fillPrice - averagePrice : averagePrice - fillPrice;
        var realised = Round(perUnit * closing, 2);
        var after = quantity + signed;

        var newAverage = after == 0
            ? 0m
            : Math.Sign(after) == Math.Sign(quantity) ? averagePrice : fillPrice;
        return new PositionChange(after, newAverage, realised);
    }

    /// <summary>Profit or loss of an open position marked at <paramref name="price"/>.</summary>
    public static decimal Unrealised(int quantity, decimal averagePrice, decimal price)
        => Round((price - averagePrice) * quantity, 2);

    private static decimal Round(decimal value, int places) => decimal.Round(value, places, MidpointRounding.AwayFromZero);
}
