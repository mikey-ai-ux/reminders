namespace Reminders.Business.Services;

/// <summary>
/// Works out when a recurring reminder fires next. All arithmetic is done on the reminder's
/// local wall-clock time so a "9:00 daily" reminder stays at 9:00 across DST changes.
/// </summary>
public static class RecurrenceCalculator
{
    public const string None = "None";
    public const string Daily = "Daily";
    public const string Weekly = "Weekly";
    public const string Monthly = "Monthly";
    public const string Annually = "Annually";
    public const string CustomPrefix = "Custom:";

    public static bool IsRecurring(string? interval) =>
        !string.IsNullOrWhiteSpace(interval) && !string.Equals(interval, None, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the interval is one the scheduler knows how to advance.</summary>
    public static bool IsSupported(string? interval)
    {
        if (!IsRecurring(interval)) return true;
        if (interval!.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return ParseCustomDays(interval).Count > 0;
        return interval is Daily or Weekly or Monthly or Annually;
    }

    public static HashSet<DayOfWeek> ParseCustomDays(string? interval)
    {
        var days = new HashSet<DayOfWeek>();
        if (string.IsNullOrWhiteSpace(interval) || !interval.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
            return days;

        foreach (var part in interval[CustomPrefix.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<DayOfWeek>(part, ignoreCase: true, out var day) && Enum.IsDefined(day))
                days.Add(day);
        }
        return days;
    }

    public static string FormatCustomDays(IEnumerable<DayOfWeek> days) =>
        CustomPrefix + string.Join(",", days.Distinct().OrderBy(d => (int)d));

    /// <summary>
    /// Returns the first occurrence strictly after <paramref name="afterUtc"/>, or null when the
    /// interval is one-time or not recognised (callers should then deactivate the reminder).
    /// </summary>
    /// <param name="anchorDay">Original day of month for Monthly/Annually, so the 31st does not
    /// drift to the 28th after February.</param>
    public static DateTime? GetNextOccurrenceUtc(DateTime occursAtUtc, string? interval, string? timeZoneId, DateTime afterUtc, int? anchorDay = null)
    {
        if (!IsRecurring(interval) || !IsSupported(interval)) return null;

        var tz = ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(occursAtUtc, DateTimeKind.Utc), tz);
        var localAfter = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), tz);
        var timeOfDay = local.TimeOfDay;

        IEnumerable<DateTime> candidates;
        if (interval!.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var days = ParseCustomDays(interval);
            candidates = DailyFrom(local, localAfter, timeOfDay, 1).Where(d => days.Contains(d.DayOfWeek));
        }
        else
        {
            candidates = interval switch
            {
                Daily => DailyFrom(local, localAfter, timeOfDay, 1),
                Weekly => DailyFrom(local, localAfter, timeOfDay, 7),
                Monthly => MonthlyFrom(local, localAfter, timeOfDay, anchorDay ?? local.Day, 1),
                Annually => MonthlyFrom(local, localAfter, timeOfDay, anchorDay ?? local.Day, 12),
                _ => []
            };
        }

        foreach (var candidate in candidates)
        {
            var utc = ToUtc(candidate, tz);
            if (utc > afterUtc) return utc;
        }
        return null;
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>Converts a local wall-clock time to UTC, moving times skipped by DST forward.</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(unspecified))
            unspecified = unspecified.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    // Steps in whole days from the original date, starting near "after" so long-idle reminders don't loop for years.
    private static IEnumerable<DateTime> DailyFrom(DateTime local, DateTime localAfter, TimeSpan timeOfDay, int stepDays)
    {
        var elapsedDays = Math.Max(0, (int)(localAfter.Date - local.Date).TotalDays);
        var steps = Math.Max(0, elapsedDays / stepDays - 1);
        var date = local.Date.AddDays((long)steps * stepDays);

        for (var i = 0; i < 400 * Math.Max(1, 7 / stepDays) + 14; i++)
        {
            date = date.AddDays(stepDays);
            yield return date + timeOfDay;
        }
    }

    private static IEnumerable<DateTime> MonthlyFrom(DateTime local, DateTime localAfter, TimeSpan timeOfDay, int anchorDay, int stepMonths)
    {
        anchorDay = Math.Clamp(anchorDay, 1, 31);
        var elapsedMonths = Math.Max(0, (localAfter.Year - local.Year) * 12 + localAfter.Month - local.Month);
        var steps = Math.Max(0, elapsedMonths / stepMonths - 1);
        var first = new DateTime(local.Year, local.Month, 1).AddMonths(steps * stepMonths);

        for (var i = 1; i <= 60; i++)
        {
            var month = first.AddMonths(i * stepMonths);
            var day = Math.Min(anchorDay, DateTime.DaysInMonth(month.Year, month.Month));
            yield return new DateTime(month.Year, month.Month, day) + timeOfDay;
        }
    }
}
