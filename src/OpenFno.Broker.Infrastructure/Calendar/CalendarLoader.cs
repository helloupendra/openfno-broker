using System.Text.Json;
using System.Text.Json.Serialization;
using OpenFno.Broker.Domain.Market;

namespace OpenFno.Broker.Infrastructure.Calendar;

/// <summary>Reads every <c>*.json</c> calendar file in a folder (one per year) into one <see cref="ExchangeCalendar"/>.</summary>
public static class CalendarLoader
{
    private static readonly JsonSerializerOptions Json = CreateOptions();

    public static ExchangeCalendar Load(string directory)
    {
        var holidays = new List<Holiday>();
        var sessions = new List<SpecialSession>();
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                using var stream = File.OpenRead(path);
                var file = JsonSerializer.Deserialize<CalendarFile>(stream, Json)
                           ?? throw new FormatException($"{path} holds no calendar.");
                holidays.AddRange(file.Holidays);
                sessions.AddRange(file.SpecialSessions);
            }
        }
        return new ExchangeCalendar(holidays, sessions);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper));
        return options;
    }

    private sealed record CalendarFile(IReadOnlyList<Holiday> Holidays, IReadOnlyList<SpecialSession> SpecialSessions);
}
