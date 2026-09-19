using OpenFno.Broker.Api.Http;
using OpenFno.Broker.Application.Engine;
using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Api.Endpoints;

/// <summary>
/// The web console is a single-page app served from wwwroot. Any path that is
/// not an API route gets its index page, so the console's own routes survive
/// a reload; an unknown API route still gets a JSON 404, never HTML.
/// </summary>
public static class WebConsoleEndpoints
{
    private static readonly string[] ApiPrefixes = ["/api", "/admin", "/openapi", "/health"];

    public static void MapWebConsole(this WebApplication app)
    {
        // Lets a client, or the console's trader terminal, see the address the
        // static-IP check will compare, which behind NAT or a proxy is rarely obvious.
        app.MapGet("/api/v1/whoami", (HttpContext http) => Results.Ok(new { ip = http.Trace().ClientIp }))
            .WithTags("Session")
            .WithSummary("The IP address the broker sees for this caller");

        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (ApiPrefixes.Any(prefix => path.StartsWithSegments(prefix)))
            {
                await ApiResults.Error(context, BrokerError.NotFound(ErrorCodes.NotFound, $"No API route {path}."))
                    .ExecuteAsync(context);
                return;
            }

            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            if (!index.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("The web console is not built. Run `npm run build` in web/.");
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.SendFileAsync(index);
        });
    }
}
