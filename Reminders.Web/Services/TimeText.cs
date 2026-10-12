using System.Globalization;
using Reminders.Business.Services;

namespace Reminders.Web.Services;

/// <summary>Friendly wording for reminder times ("Tomorrow at 9:00 AM", "in 3 hours").</summary>
public static class TimeText
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>"Today at 9:00 AM", "Tomorrow at 9:00 AM", "Wed at 9:00 AM", or a full date further out.</summary>
    public static string Day(DateTime utc, string? timeZoneId, DateTime nowUtc)
    {
        var tz = RecurrenceCalculator.ResolveTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz).Date;
        var days = (local.Date - today).TotalDays;
        var time = local.ToString("h:mm tt", Culture);

        return days switch
        {
            0 => $"Today at {time}",
            1 => $"Tomorrow at {time}",
            -1 => $"Yesterday at {time}",
            > 1 and < 7 => $"{local.ToString("dddd", Culture)} at {time}",
            _ => local.Year == today.Year
                ? $"{local.ToString("ddd, MMM d", Culture)} at {time}"
                : $"{local.ToString("MMM d, yyyy", Culture)} at {time}"
        };
    }

    /// <summary>The same wording for use mid-sentence: "today at 9:00 AM", but "Wednesday at 9:00 AM" stays capitalised.</summary>
    public static string DayInline(DateTime utc, string? timeZoneId, DateTime nowUtc)
    {
        var text = Day(utc, timeZoneId, nowUtc);
        return text.StartsWith("Today", StringComparison.Ordinal) || text.StartsWith("Tomorrow", StringComparison.Ordinal) || text.StartsWith("Yesterday", StringComparison.Ordinal)
            ? char.ToLowerInvariant(text[0]) + text[1..]
            : text;
    }

    /// <summary>"in 5 min", "in 3 hours", "in 12 days", or "2 days ago" for the past.</summary>
    public static string Relative(DateTime utc, DateTime nowUtc)
    {
        var span = DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var future = span > TimeSpan.Zero;
        var abs = span.Duration();

        string amount;
        if (abs.TotalMinutes < 1) return future ? "in under a minute" : "just now";
        if (abs.TotalMinutes < 60) amount = $"{(int)Math.Round(abs.TotalMinutes)} min";
        else if (abs.TotalHours < 24) amount = Plural((int)Math.Round(abs.TotalHours), "hour");
        else if (abs.TotalDays < 60) amount = Plural((int)Math.Round(abs.TotalDays), "day");
        else if (abs.TotalDays < 365) amount = Plural((int)Math.Round(abs.TotalDays / 30.44), "month");
        else amount = Plural((int)Math.Round(abs.TotalDays / 365.25), "year");

        return future ? $"in {amount}" : $"{amount} ago";
    }

    /// <summary>"One-time", "Every day", "Every week", "Every month", "Every year", "Every Mon, Wed".</summary>
    public static string Recurrence(string? interval)
    {
        var days = RecurrenceCalculator.ParseCustomDays(interval);
        if (days.Count == 7) return "Every day";
        if (days.Count > 0)
            return "Every " + string.Join(", ", days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()[..3]));

        return interval switch
        {
            RecurrenceCalculator.Daily => "Every day",
            RecurrenceCalculator.Weekly => "Every week",
            RecurrenceCalculator.Monthly => "Every month",
            RecurrenceCalculator.Annually => "Every year",
            _ => "One-time"
        };
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}
