using OpenFno.Broker.Domain.Orders;

namespace OpenFno.Broker.Api.Http;

public sealed record LoginRequest(string AppId, string AppSecret, string ClientId, string Totp);

public sealed record PlaceOrderRequest(
    string Symbol,
    OrderSide Side,
    int Quantity,
    OrderType Type,
    ProductType Product,
    Validity? Validity = null,
    decimal? LimitPrice = null,
    decimal? TriggerPrice = null,
    string? Tag = null)
{
    public OrderIntent ToIntent() => new(Side, Quantity, Type, Product, Validity ?? Domain.Orders.Validity.Day, LimitPrice, TriggerPrice);
}

public sealed record ModifyOrderRequest(
    int? Quantity = null,
    OrderType? Type = null,
    Validity? Validity = null,
    decimal? LimitPrice = null,
    decimal? TriggerPrice = null);

public sealed record StaticIpsRequest(IReadOnlyList<string> StaticIps);

public sealed record OpenAccountRequest(string Name, string? Profile = null);

public sealed record FundsRequest(decimal Amount, string? Reference = null);

public sealed record RegisterAppRequest(IReadOnlyList<string>? StaticIps = null);

public sealed record SetQuoteRequest(
    string Symbol,
    decimal LastPrice,
    decimal? Bid = null,
    decimal? Ask = null,
    decimal? PreviousClose = null);

public sealed record ErrorBody(ErrorDetail Error);

public sealed record ErrorDetail(string Code, string Message);
