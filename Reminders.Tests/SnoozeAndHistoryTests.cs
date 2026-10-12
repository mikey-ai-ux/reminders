using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reminders.Business.Interfaces;
using Reminders.Business.Services;
using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Tests;

public class SnoozeAndHistoryTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly RecordingSender _sender = new();
    private readonly ServiceProvider _services;
    private readonly ReminderScheduler _scheduler;

    public SnoozeAndHistoryTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _database.NewContext());
        services.AddScoped<INotificationService, NotificationService>();
        services.AddSingleton<ISubscriptionService>(new FakeSubscriptionService());
        services.AddSingleton<IEmailNotificationSender>(_sender);
        services.AddSingleton<ISmsNotificationSender>(_sender);
        services.AddSingleton<IVoiceNotificationSender>(_sender);
        services.AddSingleton<IPushNotificationSender>(_sender);
        _services = services.BuildServiceProvider();
        _scheduler = new ReminderScheduler(_services, NullLogger<ReminderScheduler>.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
        _database.Dispose();
    }

    private int AddReminder(AppUser user, DateTime occursAtUtc, string interval = "None", bool active = true)
    {
        using var db = _database.NewContext();
        var reminder = new Reminder
        {
            UserId = user.Id,
            Name = "Stretch",
            OccursAt = occursAtUtc,
            TimeZoneId = "UTC",
            RecurringInterval = interval,
            IsActive = active,
            Channels = [new ReminderChannel { Channel = NotificationChannel.Email }],
            Targets = [new ReminderTarget { Channel = NotificationChannel.Email, TargetType = "EmailAddress", TargetValue = user.Email! }]
        };
        db.Reminders.Add(reminder);
        db.SaveChanges();
        return reminder.Id;
    }

    private Reminder Load(int id)
    {
        using var db = _database.NewContext();
        return db.Reminders.Single(r => r.Id == id);
    }

    private async Task<T> WithService<T>(Func<ReminderService, Task<T>> action)
    {
        using var db = _database.NewContext();
        return await action(new ReminderService(db));
    }

    // ---- Snooze -------------------------------------------------------------------------

    [Fact]
    public async Task Snooze_OnAFiredOneTimeReminder_SendsItOnceMore_ThenItIsFinished()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddMinutes(-1));

        await _scheduler.ProcessDueRemindersAsync(now);
        Assert.Single(_sender.Emails);
        Assert.False(Load(id).IsActive);

        Assert.True(await WithService(s => s.SnoozeAsync(id, user.Id, TimeSpan.FromMinutes(10))));
        var snoozedUntil = Load(id).SnoozedUntil;
        Assert.NotNull(snoozedUntil);
        Assert.InRange(snoozedUntil.Value, DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));

        await _scheduler.ProcessDueRemindersAsync(DateTime.UtcNow.AddMinutes(5)); // not yet
        Assert.Single(_sender.Emails);

        await _scheduler.ProcessDueRemindersAsync(DateTime.UtcNow.AddMinutes(11));
        await _scheduler.ProcessDueRemindersAsync(DateTime.UtcNow.AddMinutes(12));
        Assert.Equal(2, _sender.Emails.Count);

        var stored = Load(id);
        Assert.Null(stored.SnoozedUntil);
        Assert.False(stored.IsActive);
    }

    [Fact]
    public async Task Snooze_OnARecurringReminder_AddsOneExtraNotification_WithoutMovingTheSchedule()
    {
        var user = _database.AddUser();
        var next = DateTime.UtcNow.AddDays(1);
        var id = AddReminder(user, next, "Daily");

        await WithService(s => s.SnoozeAsync(id, user.Id, TimeSpan.FromMinutes(10)));
        await _scheduler.ProcessDueRemindersAsync(DateTime.UtcNow.AddMinutes(11));

        Assert.Single(_sender.Emails);
        var stored = Load(id);
        Assert.Null(stored.SnoozedUntil);
        Assert.True(stored.IsActive);
        Assert.Equal(next, stored.OccursAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Snooze_IsLimitedToAWeek_AndToAtLeastAMinute()
    {
        var user = _database.AddUser();
        var id = AddReminder(user, DateTime.UtcNow.AddDays(1));

        await WithService(s => s.SnoozeAsync(id, user.Id, TimeSpan.FromDays(400)));
        Assert.InRange(Load(id).SnoozedUntil!.Value, DateTime.UtcNow.AddDays(6.9), DateTime.UtcNow.AddDays(7.1));

        await WithService(s => s.SnoozeAsync(id, user.Id, TimeSpan.FromSeconds(-30)));
        Assert.InRange(Load(id).SnoozedUntil!.Value, DateTime.UtcNow.AddSeconds(30), DateTime.UtcNow.AddSeconds(90));
    }

    [Fact]
    public async Task Snooze_AndDone_OnlyWorkForTheOwner()
    {
        var owner = _database.AddUser("owner@example.com");
        var other = _database.AddUser("other@example.com");
        var id = AddReminder(owner, DateTime.UtcNow.AddDays(1));

        Assert.False(await WithService(s => s.SnoozeAsync(id, other.Id, TimeSpan.FromMinutes(10))));
        Assert.False(await WithService(s => s.MarkDoneAsync(id, other.Id, skipUpcoming: true)));

        var stored = Load(id);
        Assert.Null(stored.SnoozedUntil);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task Pausing_CancelsAPendingSnooze()
    {
        var user = _database.AddUser();
        var id = AddReminder(user, DateTime.UtcNow.AddDays(1), "Daily");

        await WithService(s => s.SnoozeAsync(id, user.Id, TimeSpan.FromMinutes(10)));
        await WithService(s => s.PauseAsync(id, user.Id));
        await _scheduler.ProcessDueRemindersAsync(DateTime.UtcNow.AddMinutes(11));

        Assert.Empty(_sender.Emails);
        Assert.Null(Load(id).SnoozedUntil);
    }

    // ---- Mark done ----------------------------------------------------------------------

    [Fact]
    public async Task Done_FromANotification_CancelsTheSnooze_AndLeavesARecurringScheduleAlone()
    {
        var user = _database.AddUser();
        var next = DateTime.UtcNow.AddDays(1);
        var id = AddReminder(user, next, "Daily");
        await WithService(s => s.SnoozeAsync(id, user.Id, TimeSpan.FromMinutes(10)));

        Assert.True(await WithService(s => s.MarkDoneAsync(id, user.Id, skipUpcoming: false)));
        await _scheduler.ProcessDueRemindersAsync(DateTime.UtcNow.AddMinutes(11));

        Assert.Empty(_sender.Emails);
        var stored = Load(id);
        Assert.True(stored.IsActive);
        Assert.Equal(next, stored.OccursAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Done_FromTheList_FinishesAOneTimeReminderBeforeItFires()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddMinutes(5));

        await WithService(s => s.MarkDoneAsync(id, user.Id, skipUpcoming: true));
        await _scheduler.ProcessDueRemindersAsync(now.AddMinutes(6));

        Assert.Empty(_sender.Emails);
        Assert.False(Load(id).IsActive);
    }

    [Fact]
    public async Task Done_FromTheList_SkipsARecurringReminderToItsNextOccurrence()
    {
        var user = _database.AddUser();
        var upcoming = DateTime.UtcNow.AddHours(2);
        var id = AddReminder(user, upcoming, "Daily");

        await WithService(s => s.MarkDoneAsync(id, user.Id, skipUpcoming: true));
        await _scheduler.ProcessDueRemindersAsync(upcoming.AddMinutes(1));

        Assert.Empty(_sender.Emails); // today's was skipped
        var stored = Load(id);
        Assert.True(stored.IsActive);
        Assert.Equal(upcoming.AddDays(1), stored.OccursAt, TimeSpan.FromSeconds(1));
    }

    // ---- History paging -----------------------------------------------------------------

    private void AddHistory(AppUser user, int count, string name = "Stretch")
    {
        var reminderId = AddReminder(user, DateTime.UtcNow.AddDays(1));
        using var db = _database.NewContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            db.ReminderNotifications.Add(new ReminderNotification
            {
                ReminderId = reminderId,
                ReminderName = $"{name} {i:000}",
                MessageSnapshot = i % 10 == 0 ? "contains the word budget" : "plain message",
                Channel = i % 2 == 0 ? NotificationChannel.Email : NotificationChannel.Push,
                Status = i % 5 == 0 ? NotificationStatus.Failed : NotificationStatus.Sent,
                ScheduledForUtc = start.AddMinutes(i),
                SentAt = i % 5 == 0 ? null : start.AddMinutes(i),
                CreatedAt = start.AddMinutes(i)
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task History_ReturnsOnePageAtATime_NewestFirst_WithNoGapsOrRepeats()
    {
        var user = _database.AddUser();
        AddHistory(user, 60);

        var seen = new List<int>();
        for (var pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Page = pageNumber, PageSize = 25 }));
            Assert.Equal(60, page.TotalCount);
            Assert.Equal(3, page.TotalPages);
            Assert.Equal(pageNumber == 3 ? 10 : 25, page.Items.Count);
            seen.AddRange(page.Items.Select(n => n.Id));
        }

        Assert.Equal(60, seen.Distinct().Count());
        var first = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery()));
        Assert.Equal("Stretch 059", first.Items[0].ReminderName);
    }

    [Fact]
    public async Task History_FiltersInTheDatabase_AndCountsOnlyMatches()
    {
        var user = _database.AddUser();
        AddHistory(user, 60);

        var failed = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Status = NotificationStatus.Failed }));
        Assert.Equal(12, failed.TotalCount);
        Assert.All(failed.Items, n => Assert.Equal(NotificationStatus.Failed, n.Status));

        var push = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Channel = NotificationChannel.Push }));
        Assert.Equal(30, push.TotalCount);

        var search = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Search = "budget" }));
        Assert.Equal(6, search.TotalCount);

        var byName = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Search = "Stretch 007" }));
        Assert.Single(byName.Items);

        var anyCase = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Search = "sTRETCH 007" }));
        Assert.Single(anyCase.Items);

        // Wildcard characters are matched literally, not treated as "anything".
        var wildcard = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Search = "%" }));
        Assert.Equal(0, wildcard.TotalCount);
        var underscore = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Search = "Stretch_007" }));
        Assert.Equal(0, underscore.TotalCount);

        var oldest = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Sort = "sent_asc", PageSize = 5 }));
        Assert.Equal("Stretch 000", oldest.Items[0].ReminderName);
    }

    [Fact]
    public async Task History_ClampsOutOfRangePagesAndSizes_AndNeverShowsAnotherUsersRows()
    {
        var user = _database.AddUser("mine@example.com");
        var other = _database.AddUser("theirs@example.com");
        AddHistory(user, 30);
        AddHistory(other, 7, "Private");

        var beyond = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { Page = 99, PageSize = 25 }));
        Assert.Equal(2, beyond.Page);
        Assert.Equal(5, beyond.Items.Count);

        var huge = await WithService(s => s.GetNotificationHistoryPageAsync(user.Id, new NotificationHistoryQuery { PageSize = 100000 }));
        Assert.Equal(NotificationHistoryQuery.MaxPageSize, huge.PageSize);
        Assert.Equal(30, huge.TotalCount);
        Assert.DoesNotContain(huge.Items, n => n.ReminderName.StartsWith("Private"));

        var empty = await WithService(s => s.GetNotificationHistoryPageAsync(_database.AddUser("new@example.com").Id, new NotificationHistoryQuery()));
        Assert.Equal(0, empty.TotalCount);
        Assert.Equal(1, empty.TotalPages);
        Assert.Empty(empty.Items);
    }
}
