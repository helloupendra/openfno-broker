using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFno.Broker.Domain.Events;

namespace OpenFno.Broker.Api.Http;

public static class ApiJson
{
    /// <summary>A copy of the API's JSON settings, for serializing outside the response pipeline (request-log bodies).</summary>
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// camelCase names, enums as UPPER_SNAKE strings (never numbers), and
    /// missing or null required fields refused rather than defaulted.
    /// </summary>
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        options.RespectNullableAnnotations = true;
        options.RespectRequiredConstructorParameters = true;
        options.AllowOutOfOrderMetadataProperties = true;
        return options;
    }

    /// <summary>An event as a client may see it: secrets and their hashes are not shown, even encrypted.</summary>
    public static BrokerEvent Redact(BrokerEvent e) => e switch
    {
        AccountOpened opened => opened with { ProtectedTotpSecret = Hidden },
        AppRegistered registered => registered with { SecretHash = Hidden },
        SessionOpened opened => opened with { TokenHash = Hidden },
        SessionClosed closed => closed with { TokenHash = Hidden },
        _ => e,
    };

    private const string Hidden = "(hidden)";
}
