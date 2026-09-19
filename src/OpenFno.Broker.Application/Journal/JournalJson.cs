using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenFno.Broker.Application.Journal;

/// <summary>How events are written to the journal and shown to clients.</summary>
public static class JournalJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            // Postgres jsonb reorders keys, so the "event" discriminator is not
            // always first when an event is read back.
            AllowOutOfOrderMetadataProperties = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }
}
