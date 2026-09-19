using OpenFno.Broker.Domain.Events;
using OpenFno.Broker.Domain.Instruments;
using OpenFno.Broker.Domain.Orders;
using OpenFno.Broker.Domain.Time;
using OpenFno.Broker.Domain.Trading;

namespace OpenFno.Broker.Application.Engine;

public sealed record DayCloseReport(DateOnly TradingDate, int OrdersExpired, int AccountsSettled);

/// <summary>
/// The end of a trading day: day orders expire, intraday positions still open
/// are closed, contracts that expired are settled, carried futures are marked
/// to market, delivery trades move into holdings, and each account's profit,
/// loss and charges are posted to its ledger.
/// </summary>
public sealed partial class BrokerEngine
{
    /// <summary>Closes the latest trading date that is due and not yet closed. Run periodically.</summary>
    public async Task<DayCloseReport?> CloseTradingDayIfDueAsync(CancellationToken cancellationToken = default)
    {
        var due = await ReadAsync<DateOnly?>(now =>
        {
            var today = Ist.DateOf(now);
            var target = Ist.TimeOf(now) >= _options.SettlementTime ? today : today.AddDays(-1);
            return _state.LastClosedDate is { } closed && closed >= target ? (DateOnly?)null : target;
        }, cancellationToken);
        if (!due.IsSuccess || due.Value is not { } date) return null;
        return (await CloseTradingDayAsync(date, cancellationToken)).Value;
    }

    /// <summary>
    /// Closes <paramref name="tradingDate"/> now, whatever the time: the sandbox's
    /// "end the day" button, and the scheduled close. Uses the latest quotes as
    /// settlement prices.
    /// </summary>
    public async Task<Result<DayCloseReport>> CloseTradingDayAsync(DateOnly tradingDate, CancellationToken cancellationToken = default)
    {
        // Orders first: whatever still works on or before the date expires.
        var expired = await RunAsync<int>(_ =>
        {
            var events = new List<BrokerEvent>();
            foreach (var orderId in _state.LiveOrderIds)
            {
                var order = _state.Orders[orderId];
                if (order.Ticket.TradingDate > tradingDate) continue;
                var clientId = order.Ticket.ClientId;
                if (order.Status == OrderStatus.Transit)
                    events.Add(new OrderAccepted { ClientId = clientId, OrderId = orderId, Status = order.Intent.IsStop ? OrderStatus.TriggerPending : OrderStatus.Open });
                events.Add(new OrderExpired { ClientId = clientId, OrderId = orderId });
            }
            return Outcome<int>.Of(events, _ => events.Count(e => e is OrderExpired));
        }, null, cancellationToken);
        if (!expired.IsSuccess) return expired.Error!;

        var clients = await ReadAsync<IReadOnlyList<string>>(
            _ => Result<IReadOnlyList<string>>.Ok(_state.Accounts.Keys.Order(StringComparer.Ordinal).ToList()), cancellationToken);

        var settled = 0;
        foreach (var clientId in clients.Value)
        {
            var result = await RunAsync<bool>(_ =>
            {
                var account = _state.Account(clientId);
                var entries = SettlementEntries(account, tradingDate);
                var nothingToDo = entries.Count == 0 && account.DayRealised == 0 && account.DayCharges == 0
                                  && !account.Positions.Values.Any(p => p.Quantity == 0);
                if (nothingToDo) return Outcome<bool>.Nothing(false);
                return Outcome<bool>.Of(new DaySettled
                {
                    ClientId = clientId,
                    TradingDate = tradingDate,
                    Entries = entries,
                    Realised = account.DayRealised + entries.Sum(e => e.Realised),
                    Charges = account.DayCharges,
                }, _ => true);
            }, null, cancellationToken);
            if (result.IsSuccess && result.Value) settled++;
        }

        await RunAsync<bool>(_ => Outcome<bool>.Of(
            new TradingDayClosed { ClientId = TradingDayClosed.Broker, TradingDate = tradingDate }, _ => true), null, cancellationToken);

        return new DayCloseReport(tradingDate, expired.Value, settled);
    }

    private List<SettlementEntry> SettlementEntries(AccountState account, DateOnly tradingDate)
    {
        var entries = new List<SettlementEntry>();
        foreach (var position in account.Positions.Values.Where(p => p.Quantity != 0).OrderBy(p => p.Symbol, StringComparer.Ordinal))
        {
            var instrument = _instruments.Find(position.Symbol);
            var last = _quotes.Find(position.Symbol)?.LastPrice;
            var flat = new PositionSnapshot(0, 0m, 0m);

            if (position.Product == ProductType.Mis)
            {
                var price = last ?? position.AveragePrice;
                entries.Add(new SettlementEntry(position.Symbol, position.Product, SettlementKind.IntradayClose,
                    Math.Abs(position.Quantity), price, 0m, Close(position, price), flat));
            }
            else if (instrument?.Expiry is { } expiry && expiry <= tradingDate)
            {
                var price = last ?? IntrinsicValue(instrument) ?? position.AveragePrice;
                entries.Add(new SettlementEntry(position.Symbol, position.Product, SettlementKind.Expiry,
                    Math.Abs(position.Quantity), price, 0m, Close(position, price), flat));
            }
            else if (position.Product == ProductType.Cnc)
            {
                var quantity = Math.Abs(position.Quantity);
                var value = decimal.Round(quantity * position.AveragePrice, 2, MidpointRounding.AwayFromZero);
                entries.Add(position.Quantity > 0
                    ? new SettlementEntry(position.Symbol, position.Product, SettlementKind.DeliveryIn, quantity, position.AveragePrice, -value, 0m, flat)
                    : new SettlementEntry(position.Symbol, position.Product, SettlementKind.DeliveryOut, quantity, position.AveragePrice, value, 0m, flat));
            }
            else if (instrument?.Kind == InstrumentKind.Future && last is { } settle)
            {
                // Futures settle their difference daily; the position carries at the settlement price.
                var realised = PositionMath.Unrealised(position.Quantity, position.AveragePrice, settle);
                var margin = PositionMargin(account, instrument, position.Product, position.Quantity, settle);
                entries.Add(new SettlementEntry(position.Symbol, position.Product, SettlementKind.MarkToMarket,
                    Math.Abs(position.Quantity), settle, 0m, realised, new PositionSnapshot(position.Quantity, settle, margin)));
            }
            // Carried options keep their price: their premium was paid in full.
        }
        return entries;
    }

    private static decimal Close(PositionState position, decimal price)
        => PositionMath.Unrealised(position.Quantity, position.AveragePrice, price);

    /// <summary>An option's value at expiry from its underlying's last price, when the feed has one.</summary>
    private decimal? IntrinsicValue(Instrument option)
    {
        if (option.Kind != InstrumentKind.Option || option.Strike is not { } strike) return null;
        var spot = UnderlyingSymbols(option).Select(s => _quotes.Find(s)?.LastPrice).FirstOrDefault(p => p is not null);
        if (spot is null) return null;
        return option.Right == OptionRight.Call ? Math.Max(0m, spot.Value - strike) : Math.Max(0m, strike - spot.Value);
    }

    private static IEnumerable<string> UnderlyingSymbols(Instrument option)
    {
        var exchange = option.Exchange == Exchange.Bse ? "BSE" : "NSE";
        yield return option.Underlying.ToUpperInvariant() switch
        {
            "NIFTY" => "NSE:NIFTY50-INDEX",
            "BANKNIFTY" => "NSE:NIFTYBANK-INDEX",
            "FINNIFTY" => "NSE:FINNIFTY-INDEX",
            "MIDCPNIFTY" => "NSE:MIDCPNIFTY-INDEX",
            "NIFTYNXT50" => "NSE:NIFTYNXT50-INDEX",
            "SENSEX" => "BSE:SENSEX-INDEX",
            "BANKEX" => "BSE:BANKEX-INDEX",
            var stock => $"{exchange}:{stock}-EQ",
        };
    }
}
