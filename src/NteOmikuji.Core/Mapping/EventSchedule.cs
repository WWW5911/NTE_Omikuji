using System.Globalization;

namespace NteOmikuji.Core.Mapping;

public enum SchedulePhase
{
    Upcoming,
    Active,
    Ended,
}

/// <summary>One announced event in schedule.csv. A new event's ID stays blank until a capture reveals it, because a guessed ID would never match.</summary>
/// <param name="Ends">The day the event closes. Events end in the early morning, so that day counts as over.</param>
public sealed record ScheduledEvent(
    string Id,
    string Version,
    int Phase,
    bool IsArc,
    bool IsRerun,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Featured,
    string? EventId,
    DateOnly Starts,
    DateOnly Ends,
    bool IsEstimate)
{
    public string Name(string locale) => Localized(Names, locale);

    public string FeaturedName(string locale) => Localized(Featured, locale);

    public SchedulePhase PhaseOn(DateOnly today) =>
        today < Starts ? SchedulePhase.Upcoming : today < Ends ? SchedulePhase.Active : SchedulePhase.Ended;

    public int DaysUntilStart(DateOnly today) => Starts.DayNumber - today.DayNumber;

    public int DaysLeft(DateOnly today) => Ends.DayNumber - today.DayNumber;

    private static string Localized(IReadOnlyDictionary<string, string> names, string locale) =>
        names.GetValueOrDefault(locale)
        ?? names.GetValueOrDefault(RewardSchema.Chinese)
        ?? names.GetValueOrDefault(RewardSchema.English)
        ?? names.Values.FirstOrDefault()
        ?? "";
}

public static class EventSchedule
{
    private const string FeaturedPrefix = "featured.";
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Reads the table, reporting rows it cannot use instead of failing on them.</summary>
    public static IReadOnlyList<ScheduledEvent> Parse(CsvTable table, string file, List<string> issues)
    {
        var entries = new List<ScheduledEvent>();
        foreach (var row in table.Rows.Where(row => row.Get("id").Length > 0))
        {
            if (!TryDate(row.Get("starts"), out var starts) || !TryDate(row.Get("ends"), out var ends))
            {
                issues.Add($"{file} 第 {row.Line} 行：日期應為 {DateFormat}。");
                continue;
            }
            int.TryParse(row.Get("phase"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var phase);
            // Files from earlier versions call this column banner_id.
            var eventId = row.Get("event_id") is { Length: > 0 } current ? current : row.Get("banner_id");
            entries.Add(new ScheduledEvent(
                row.Get("id"),
                row.Get("version"),
                phase,
                row.Get("pool").Equals("arc", StringComparison.OrdinalIgnoreCase),
                row.Get("rerun").Length > 0,
                Localized(table, row, RewardSchema.NamePrefix),
                Localized(table, row, FeaturedPrefix),
                eventId.Length > 0 ? eventId : null,
                starts,
                ends,
                row.Get("estimate").Length > 0));
        }
        return entries;
    }

    /// <summary>Events not yet over, soonest first, with each phase's boards before its Arcs and new events before reruns.</summary>
    public static IReadOnlyList<ScheduledEvent> Visible(IEnumerable<ScheduledEvent> entries, DateOnly today) =>
        entries.Where(entry => entry.PhaseOn(today) != SchedulePhase.Ended)
            .OrderBy(entry => entry.Starts)
            .ThenBy(entry => entry.IsArc)
            .ThenBy(entry => entry.IsRerun)
            .ToList();

    private static bool TryDate(string text, out DateOnly date) =>
        DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static Dictionary<string, string> Localized(CsvTable table, CsvRow row, string prefix) =>
        table.Headers.Where(header => header.StartsWith(prefix, StringComparison.Ordinal) && row.Get(header).Length > 0)
            .ToDictionary(header => header[prefix.Length..], row.Get);
}
