using Reminders.Web.Services;

namespace Reminders.Tests;

public class UiHelperTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2026, 3, 10, 15, 0, "Today at 3:00 PM")]
    [InlineData(2026, 3, 11, 9, 30, "Tomorrow at 9:30 AM")]
    [InlineData(2026, 3, 9, 9, 30, "Yesterday at 9:30 AM")]
    [InlineData(2026, 3, 14, 8, 0, "Saturday at 8:00 AM")]
    [InlineData(2026, 4, 2, 8, 0, "Thu, Apr 2 at 8:00 AM")]
    [InlineData(2027, 1, 5, 8, 0, "Jan 5, 2027 at 8:00 AM")]
    public void Day_UsesFriendlyWording(int y, int mo, int d, int h, int mi, string expected)
    {
        Assert.Equal(expected, TimeText.Day(Utc(y, mo, d, h, mi), "UTC", Utc(2026, 3, 10, 12)));
    }

    [Fact]
    public void Day_IsWorkedOutInTheRemindersTimezone()
    {
        // 03:00 UTC on the 11th is still the evening of the 10th in New York.
        Assert.Equal("Today at 11:00 PM", TimeText.Day(Utc(2026, 3, 11, 3), "America/New_York", Utc(2026, 3, 10, 20)));
    }

    [Theory]
    [InlineData(0, 0, 30, "in under a minute")]
    [InlineData(0, 5, 0, "in 5 min")]
    [InlineData(3, 0, 0, "in 3 hours")]
    [InlineData(24, 0, 0, "in 1 day")]
    [InlineData(72, 0, 0, "in 3 days")]
    [InlineData(-48, 0, 0, "2 days ago")]
    public void Relative_DescribesTheGap(int hours, int minutes, int seconds, string expected)
    {
        var now = Utc(2026, 3, 10, 12);
        Assert.Equal(expected, TimeText.Relative(now.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds), now));
    }

    [Theory]
    [InlineData("None", "One-time")]
    [InlineData("Daily", "Every day")]
    [InlineData("Weekly", "Every week")]
    [InlineData("Monthly", "Every month")]
    [InlineData("Annually", "Every year")]
    [InlineData("Custom:Monday,Wednesday", "Every Mon, Wed")]
    [InlineData("Custom:0,1,2,3,4,5,6", "Every day")]
    public void Recurrence_IsReadable(string interval, string expected)
    {
        Assert.Equal(expected, TimeText.Recurrence(interval));
    }

    [Fact]
    public void QuickTimes_AreAlwaysInTheFuture()
    {
        // Tuesday 10 March 2026, late evening.
        var now = new DateTime(2026, 3, 10, 22, 47, 30);

        Assert.Equal(new DateTime(2026, 3, 10, 23, 50, 0), QuickTimes.Resolve("hour", now));
        Assert.Equal(new DateTime(2026, 3, 11, 18, 0, 0), QuickTimes.Resolve("evening", now));
        Assert.Equal(new DateTime(2026, 3, 11, 9, 0, 0), QuickTimes.Resolve("tomorrow", now));
        Assert.Equal(new DateTime(2026, 3, 16, 9, 0, 0), QuickTimes.Resolve("monday", now));
        Assert.Contains(QuickTimes.For(now), p => p.Label == "Tomorrow evening");
    }

    [Fact]
    public void QuickTimes_OnAMonday_NextMondayIsAWeekAway_AndThisEveningIsToday()
    {
        var mondayMorning = new DateTime(2026, 3, 9, 8, 0, 0);

        Assert.Equal(new DateTime(2026, 3, 16, 9, 0, 0), QuickTimes.Resolve("monday", mondayMorning));
        Assert.Equal(new DateTime(2026, 3, 9, 18, 0, 0), QuickTimes.Resolve("evening", mondayMorning));
        Assert.Equal(new DateTime(2026, 3, 9, 9, 0, 0), QuickTimes.Resolve("hour", mondayMorning));
    }

    [Theory]
    [InlineData("🔔", "bell")]
    [InlineData("💊", "pill")]
    [InlineData("pill", "pill")]
    [InlineData("", "bell")]
    [InlineData("not-an-icon", "bell")]
    public void ReminderIcons_MapOldEmojiAndUnknownValues(string stored, string expected)
    {
        Assert.Equal(expected, Icons.ForReminder(stored));
    }

    [Fact]
    public void EveryPickableIcon_HasArtwork()
    {
        var fallback = Icons.Markup("definitely-missing");
        foreach (var name in Icons.ReminderChoices.Where(n => n != "bell"))
            Assert.NotEqual(fallback, Icons.Markup(name));
    }
}
