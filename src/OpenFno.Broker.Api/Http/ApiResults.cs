using OpenFno.Broker.Application.Engine;

namespace OpenFno.Broker.Api.Http;

public static class ApiResults
{
    /// <summary>The standard error body, <c>{"error":{"code","message"}}</c>, with the status for its kind.</summary>
    public static IResult Error(HttpContext context, BrokerError error)
    {
        if (context.Features.Get<RequestTrace>() is { } trace) trace.ErrorCode = error.Code;
        return Results.Json(new ErrorBody(new ErrorDetail(error.Code, error.Message)), statusCode: StatusFor(error.Kind));
    }

    public static IResult From<T>(HttpContext context, Result<T> result)
        => result.IsSuccess ? Results.Ok(result.Value) : Error(context, result.Error!);

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Invalid => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status503ServiceUnavailable,
    };
}
