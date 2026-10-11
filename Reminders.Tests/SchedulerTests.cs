using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Reminders.Business.Interfaces;
using Reminders.Business.Services;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Tests;

public class SchedulerTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly RecordingSender _sender = new();
    private readonly FakeSubscriptionService _subscriptions = new();
    private readonly ServiceProvider _services;
    private readonly ReminderScheduler _scheduler;

    public SchedulerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _database.NewContext());
        services.AddScoped<INotificationService, NotificationService>();
        services.AddSingleton<ISubscriptionService>(_subscriptions);
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

    private int AddReminder(AppUser user, DateTime occursAtUtc, string interval = "None", Action<Reminder>? configure = null)
    {
        using var db = _database.NewContext();
        var reminder = new Reminder
        {
            UserId = user.Id,
            Name = "Standup",
            OccursAt = occursAtUtc,
            TimeZoneId = "UTC",
            RecurringInterval = interval,
            Channels = [new ReminderChannel { Channel = NotificationChannel.Email }],
            Targets = [new ReminderTarget { Channel = NotificationChannel.Email, TargetType = "EmailAddress", TargetValue = user.Email! }]
        };
        configure?.Invoke(reminder);
        db.Reminders.Add(reminder);
        db.SaveChanges();
        return reminder.Id;
    }

    private Reminder Load(int id)
    {
        using var db = _database.NewContext();
        return db.Reminders.Include(r => r.Notifications).Single(r => r.Id == id);
    }

    [Fact]
    public async Task DueOneTimeReminder_IsSentOnce_ThenDeactivated()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddMinutes(-1));

        await _scheduler.ProcessDueRemindersAsync(now);
        await _scheduler.ProcessDueRemindersAsync(now.AddSeconds(20));
        await _scheduler.ProcessDueRemindersAsync(now.AddMinutes(5));

        Assert.Equal([user.Email!], _sender.Emails);
        var stored = Load(id);
        Assert.False(stored.IsActive);
        Assert.Single(stored.Notifications);
        Assert.Equal(NotificationStatus.Sent, stored.Notifications.Single().Status);
    }

    [Fact]
    public async Task FutureAndPausedReminders_AreNotSent()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        AddReminder(user, now.AddMinutes(10));
        AddReminder(user, now.AddMinutes(-10), configure: r => r.IsPaused = true);
        AddReminder(user, now.AddMinutes(-10), configure: r => r.IsActive = false);

        await _scheduler.ProcessDueRemindersAsync(now);

        Assert.Empty(_sender.Emails);
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Annually")]
    [InlineData("Custom:0,1,2,3,4,5,6")]
    public async Task RecurringReminder_FiresOncePerOccurrence_NeverEveryPoll(string interval)
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddMinutes(-1), interval);

        // Three scheduler passes inside the same occurrence.
        await _scheduler.ProcessDueRemindersAsync(now);
        await _scheduler.ProcessDueRemindersAsync(now.AddSeconds(20));
        await _scheduler.ProcessDueRemindersAsync(now.AddMinutes(3));

        Assert.Single(_sender.Emails);
        var stored = Load(id);
        Assert.True(stored.IsActive);
        Assert.True(stored.OccursAt > now.AddMinutes(3));
    }

    [Fact]
    public async Task RecurringReminderMissedForDays_SendsOneCatchUp_NotOnePerMissedDay()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddDays(-10), "Daily");

        for (var i = 0; i < 5; i++)
            await _scheduler.ProcessDueRemindersAsync(now.AddSeconds(20 * i));

        Assert.Single(_sender.Emails);
        Assert.True(Load(id).OccursAt > now);
    }

    [Fact]
    public async Task ReminderWithUnsupportedInterval_IsDeactivatedInsteadOfLoopingForever()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddMinutes(-1), "Custom");

        await _scheduler.ProcessDueRemindersAsync(now);
        await _scheduler.ProcessDueRemindersAsync(now.AddMinutes(1));

        Assert.Single(_sender.Emails);
        Assert.False(Load(id).IsActive);
    }

    [Fact]
    public async Task OneFailingTarget_DoesNotStopTheOthers_AndEachGetsItsOwnHistoryRow()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        _sender.FailFor.Add("broken@example.com");
        var id = AddReminder(user, now.AddMinutes(-1), configure: r =>
        {
            r.Targets.Clear();
            r.Targets.Add(new ReminderTarget { Channel = NotificationChannel.Email, TargetType = "EmailAddress", TargetValue = "broken@example.com" });
            r.Targets.Add(new ReminderTarget { Channel = NotificationChannel.Email, TargetType = "EmailAddress", TargetValue = "ok@example.com" });
        });

        await _scheduler.ProcessDueRemindersAsync(now);

        Assert.Equal(["ok@example.com"], _sender.Emails);
        var statuses = Load(id).Notifications.Select(n => n.Status).OrderBy(s => s).ToList();
        Assert.Equal([NotificationStatus.Sent, NotificationStatus.Failed], statuses);
    }

    [Fact]
    public async Task PushThatReachesNoDevice_IsRecordedAsFailed()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        _sender.PushDelivered = 0;
        var id = AddReminder(user, now.AddMinutes(-1), configure: r =>
        {
            r.Channels.Clear();
            r.Channels.Add(new ReminderChannel { Channel = NotificationChannel.Push });
            r.Targets.Clear();
            r.Targets.Add(new ReminderTarget { Channel = NotificationChannel.Push, TargetType = "PushDevice", TargetValue = "https://push.example/abc" });
        });

        await _scheduler.ProcessDueRemindersAsync(now);

        Assert.Equal(1, _sender.PushCalls);
        Assert.Equal(NotificationStatus.Failed, Load(id).Notifications.Single().Status);
    }

    [Fact]
    public async Task SmsQuota_IsEnforcedPerMessage()
    {
        var user = _database.AddUser(tier: SubscriptionTier.Pro, phone: "+15550000001");
        var now = DateTime.UtcNow;
        _subscriptions.SmsLimit = 1;
        var id = AddReminder(user, now.AddMinutes(-1), configure: r =>
        {
            r.Channels.Clear();
            r.Channels.Add(new ReminderChannel { Channel = NotificationChannel.SMS });
            r.Targets.Clear();
            r.Targets.Add(new ReminderTarget { Channel = NotificationChannel.SMS, TargetType = "PhoneNumber", TargetValue = "+15550000001" });
            r.Targets.Add(new ReminderTarget { Channel = NotificationChannel.SMS, TargetType = "PhoneNumber", TargetValue = "+15550000002" });
        });

        await _scheduler.ProcessDueRemindersAsync(now);

        Assert.Single(_sender.Phones);
        Assert.Equal(1, Load(id).Notifications.Count(n => n.Status == NotificationStatus.Failed));
    }

    [Fact]
    public async Task FreePlan_DoesNotSendPaidChannels()
    {
        var user = _database.AddUser(phone: "+15550000001");
        var now = DateTime.UtcNow;
        AddReminder(user, now.AddMinutes(-1), configure: r =>
        {
            r.Channels.Add(new ReminderChannel { Channel = NotificationChannel.SMS });
            r.Targets.Add(new ReminderTarget { Channel = NotificationChannel.SMS, TargetType = "PhoneNumber", TargetValue = "+15550000001" });
        });

        await _scheduler.ProcessDueRemindersAsync(now);

        Assert.Single(_sender.Emails);
        Assert.Empty(_sender.Phones);
    }

    [Fact]
    public async Task VeryLongDescription_DoesNotBreakHistoryRecording()
    {
        var user = _database.AddUser();
        var now = DateTime.UtcNow;
        var id = AddReminder(user, now.AddMinutes(-1), configure: r => r.Description = new string('x', 5000));

        await _scheduler.ProcessDueRemindersAsync(now);

        Assert.True(Load(id).Notifications.Single().MessageSnapshot.Length <= 2000);
    }
}
