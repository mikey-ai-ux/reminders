namespace Reminders.Web.Services;

/// <summary>The timezone list shown in the profile and reminder forms, so both store ids the other understands.</summary>
public static class TimeZoneOptions
{
    public sealed record Option(string Id, string DisplayName);

    public static IReadOnlyList<Option> All { get; } = Build();

    /// <summary>Maps any known timezone id (IANA or Windows) to the id used in <see cref="All"/>; falls back to UTC.</summary>
    public static string Normalize(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (Contains(id)) return All.First(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)).Id;
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && Contains(windowsId)) return windowsId;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && Contains(ianaId)) return ianaId;
        }

        return Contains("UTC") ? "UTC" : TimeZoneInfo.Utc.Id;
    }

    /// <summary>Current wall-clock time in the given timezone.</summary>
    public static DateTime NowIn(string? id)
    {
        try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(Normalize(id))); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return DateTime.UtcNow; }
    }

    private static bool Contains(string id) => All.Any(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    private static List<Option> Build()
    {
        var options = TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(tz => tz.BaseUtcOffset)
            .ThenBy(tz => tz.DisplayName)
            .Select(tz => new Option(tz.Id, $"(UTC{FormatOffset(tz.BaseUtcOffset)}) {StripOffsetPrefix(tz.DisplayName)}"))
            .ToList();

        if (!options.Any(o => o.Id == "UTC"))
            options.Insert(0, new Option("UTC", "(UTC+00:00) Coordinated Universal Time"));

        return options;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"{sign}{offset.Duration():hh\\:mm}";
    }

    // Windows display names already start with "(UTC+01:00) "; avoid showing the offset twice.
    private static string StripOffsetPrefix(string displayName)
    {
        if (displayName.StartsWith("(UTC", StringComparison.Ordinal))
        {
            var end = displayName.IndexOf(')');
            if (end > 0 && end + 1 < displayName.Length) return displayName[(end + 1)..].TrimStart();
        }
        return displayName;
    }
}
