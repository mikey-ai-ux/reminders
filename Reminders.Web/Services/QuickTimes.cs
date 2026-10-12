namespace Reminders.Web.Services;

/// <summary>The one-tap time shortcuts offered when adding a reminder.</summary>
public static class QuickTimes
{
    public sealed record Preset(string Key, string Label);

    /// <summary>Shortcuts worded for the current time (e.g. "Tomorrow evening" once this evening has passed).</summary>
    public static IReadOnlyList<Preset> For(DateTime localNow) =>
    [
        new("hour", "In 1 hour"),
        new("evening", localNow.Hour < 18 ? "This evening" : "Tomorrow evening"),
        new("tomorrow", "Tomorrow morning"),
        new("monday", "Next Monday")
    ];

    /// <summary>Turns a shortcut into a local wall-clock time that is always in the future.</summary>
    public static DateTime Resolve(string key, DateTime localNow)
    {
        var today = localNow.Date;
        switch (key)
        {
            case "evening":
                return (localNow.Hour < 18 ? today : today.AddDays(1)).AddHours(18);
            case "tomorrow":
                return today.AddDays(1).AddHours(9);
            case "monday":
                var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
                return today.AddDays(days == 0 ? 7 : days).AddHours(9);
            default:
                // "In 1 hour", rounded up to the next 5 minutes so the time reads cleanly.
                var inAnHour = localNow.AddHours(1);
                var rounded = new DateTime(inAnHour.Year, inAnHour.Month, inAnHour.Day, inAnHour.Hour, inAnHour.Minute, 0);
                var extra = (5 - rounded.Minute % 5) % 5;
                return rounded.AddMinutes(extra);
        }
    }
}
