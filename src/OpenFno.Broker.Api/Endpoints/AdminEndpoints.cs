using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Abstractions;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Market;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Endpoints;

/// <summary>What the broker's back office does: open accounts, move money, issue API apps, and (in a sandbox) set prices.</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").WithTags("Admin").AddEndpointFilter<AdminKeyFilter>();

        admin.MapGet("accounts", async (HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.ListAccountsAsync(ct)));

        admin.MapPost("accounts", async (OpenAccountRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.OpenAccountAsync(request.Name, request.Profile ?? BrokerProfiles.Fyers.Id, ct)))
            .WithSummary("Open an account")
            .WithDescription("Returns the client ID and the TOTP secret for an authenticator app. The secret is shown only here.");

        admin.MapGet("accounts/{clientId}", async (string clientId, HttpContext http, BrokerEngine engine, CancellationToken ct)
            => ApiResults.From(http, await engine.GetAccountAsync(clientId, ct)));

        admin.MapPost("accounts/{clientId}/funds", async (string clientId, FundsRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.AddFundsAsync(clientId, request.Amount, request.Reference, ct)))
            .WithSummary("Pay in");

        admin.MapPost("accounts/{clientId}/withdrawals", async (string clientId, FundsRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.WithdrawFundsAsync(clientId, request.Amount, request.Reference, ct)))
            .WithSummary("Pay out");

        admin.MapPost("accounts/{clientId}/apps", async (string clientId, RegisterAppRequest request, HttpContext http, BrokerEngine engine, CancellationToken ct)
                => ApiResults.From(http, await engine.RegisterAppAsync(clientId, request.StaticIps, ct)))
            .WithSummary("Issue an API app")
            .WithDescription("Returns the app ID and secret; the secret is shown only here.");

        admin.MapPut("quotes", (SetQuoteRequest request, HttpContext http, IQuoteBook quotes, IClock clock) =>
            {
                if (string.IsNullOrWhiteSpace(request.Symbol) || request.LastPrice <= 0)
                    return ApiResults.Error(http, BrokerError.Invalid(ErrorCodes.InvalidRequest, "A quote needs a symbol and a last price above zero."));
                var quote = new Quote
                {
                    Symbol = request.Symbol.Trim(),
                    LastPrice = request.LastPrice,
                    At = clock.UtcNow,
                    Bid = request.Bid,
                    Ask = request.Ask,
                    PreviousClose = request.PreviousClose,
                };
                quotes.Update(quote);
                return Results.Ok(quote);
            })
            .WithSummary("Set a quote by hand")
            .WithDescription("For a sandbox without the live feed. A tick from the feed replaces it.");
    }
}
