using OpenFno.Broker.Domain.Rules;

namespace OpenFno.Broker.Application.Engine;

/// <summary>What kind of refusal an error is; the API maps it to an HTTP status.</summary>
public enum ErrorKind
{
    Invalid,
    NotFound,
    Conflict,
    Unauthorized,
    Forbidden,
    RateLimited,
    Unavailable,
}

public sealed record BrokerError(string Code, string Message, ErrorKind Kind)
{
    public static BrokerError Invalid(Rejection rejection) => new(rejection.Code, rejection.Message, ErrorKind.Invalid);
    public static BrokerError Invalid(string code, string message) => new(code, message, ErrorKind.Invalid);
    public static BrokerError NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);
    public static BrokerError Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static BrokerError Conflict(Rejection rejection) => new(rejection.Code, rejection.Message, ErrorKind.Conflict);
    public static BrokerError Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);
}

/// <summary>A value or the reason there is none.</summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(T? value, BrokerError? error)
    {
        _value = value;
        Error = error;
    }

    public BrokerError? Error { get; }

    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"No value: {Error!.Code}.");

    /// <summary>For values whose static type is an interface, which implicit conversions cannot take.</summary>
    public static Result<T> Ok(T value) => new(value, null);

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(BrokerError error) => new(default, error);
}
