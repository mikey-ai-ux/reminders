using Reminders.Business.Services;

namespace Reminders.Tests;

public class RecurrenceCalculatorTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void OneTime_HasNoNextOccurrence()
    {
        Assert.Null(RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2026, 1, 1, 9), "None", "UTC", Utc(2026, 1, 1, 9)));
    }

    [Theory]
    [InlineData("Hourly")]
    [InlineData("Custom")]
    [InlineData("Custom:")]
    [InlineData("Custom:9,x")]
    public void UnknownInterval_HasNoNextOccurrence_SoTheSchedulerCannotLoop(string interval)
    {
        Assert.False(RecurrenceCalculator.IsSupported(interval));
        Assert.Null(RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2026, 1, 1, 9), interval, "UTC", Utc(2026, 1, 1, 9)));
    }

    [Fact]
    public void Daily_MovesToTomorrow()
    {
        var next = RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2026, 3, 10, 9), "Daily", "UTC", Utc(2026, 3, 10, 9));
        Assert.Equal(Utc(2026, 3, 11, 9), next);
    }

    [Fact]
    public void Daily_AfterLongDowntime_SkipsStraightToTheNextFutureTime()
    {
        var next = RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2020, 1, 1, 9), "Daily", "UTC", Utc(2026, 3, 10, 12));
        Assert.Equal(Utc(2026, 3, 11, 9), next);
    }

    [Fact]
    public void Weekly_KeepsTheWeekday()
    {
        var first = Utc(2026, 3, 2, 18, 30); // Monday
        var next = RecurrenceCalculator.GetNextOccurrenceUtc(first, "Weekly", "UTC", Utc(2026, 3, 20));
        Assert.Equal(Utc(2026, 3, 23, 18, 30), next);
        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
    }

    [Fact]
    public void Monthly_OnThe31st_ClampsInShortMonthsButDoesNotDrift()
    {
        var jan31 = Utc(2026, 1, 31, 9);
        var feb = RecurrenceCalculator.GetNextOccurrenceUtc(jan31, "Monthly", "UTC", jan31, anchorDay: 31);
        Assert.Equal(Utc(2026, 2, 28, 9), feb);

        var mar = RecurrenceCalculator.GetNextOccurrenceUtc(feb!.Value, "Monthly", "UTC", feb.Value, anchorDay: 31);
        Assert.Equal(Utc(2026, 3, 31, 9), mar);
    }

    [Fact]
    public void Annually_MovesOneYear_AndHandlesLeapDay()
    {
        var next = RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2026, 5, 17, 8), "Annually", "UTC", Utc(2026, 5, 17, 8));
        Assert.Equal(Utc(2027, 5, 17, 8), next);

        var leap = RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2024, 2, 29, 8), "Annually", "UTC", Utc(2024, 2, 29, 8), anchorDay: 29);
        Assert.Equal(Utc(2025, 2, 28, 8), leap);
        var backOnLeapDay = RecurrenceCalculator.GetNextOccurrenceUtc(Utc(2027, 2, 28, 8), "Annually", "UTC", Utc(2027, 2, 28, 8), anchorDay: 29);
        Assert.Equal(Utc(2028, 2, 29, 8), backOnLeapDay);
    }

    [Fact]
    public void CustomDays_PicksTheNextSelectedWeekday()
    {
        var interval = RecurrenceCalculator.FormatCustomDays([DayOfWeek.Monday, DayOfWeek.Thursday]);
        Assert.Equal("Custom:Monday,Thursday", interval);

        var monday = Utc(2026, 3, 2, 7);
        var thursday = RecurrenceCalculator.GetNextOccurrenceUtc(monday, interval, "UTC", monday);
        Assert.Equal(Utc(2026, 3, 5, 7), thursday);

        var nextMonday = RecurrenceCalculator.GetNextOccurrenceUtc(thursday!.Value, interval, "UTC", thursday.Value);
        Assert.Equal(Utc(2026, 3, 9, 7), nextMonday);
    }

    [Fact]
    public void Daily_KeepsLocalTimeAcrossDaylightSaving()
    {
        // 09:00 in New York is 14:00 UTC in winter and 13:00 UTC once DST starts on 8 March 2026.
        const string tz = "America/New_York";
        var beforeDst = Utc(2026, 3, 7, 14);
        var next = RecurrenceCalculator.GetNextOccurrenceUtc(beforeDst, "Daily", tz, beforeDst);
        Assert.Equal(Utc(2026, 3, 8, 13), next);
    }

    [Fact]
    public void LocalTimeSkippedByDaylightSaving_IsMovedForwardInsteadOfThrowing()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var utc = RecurrenceCalculator.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), tz); // 02:30 does not exist that night
        Assert.Equal(Utc(2026, 3, 8, 7), utc); // 03:00 EDT
    }

    [Fact]
    public void UnknownTimeZone_FallsBackToUtc()
    {
        Assert.Equal(TimeZoneInfo.Utc, RecurrenceCalculator.ResolveTimeZone("Not/AZone"));
    }
}
