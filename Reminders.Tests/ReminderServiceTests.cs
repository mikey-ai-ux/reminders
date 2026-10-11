using Microsoft.EntityFrameworkCore;
using Reminders.Business.Services;
using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Tests;

public class ReminderServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static Reminder EmailReminder(AppUser user, DateTime occursAtUtc, string interval = "None", string name = "Pay rent") => new()
    {
        UserId = user.Id,
        Name = name,
        OccursAt = occursAtUtc,
        TimeZoneId = "UTC",
        RecurringInterval = interval,
        Channels = [new ReminderChannel { Channel = NotificationChannel.Email }],
        Targets = [new ReminderTarget { Channel = NotificationChannel.Email, TargetType = "EmailAddress", TargetValue = user.Email!, Label = user.Email }]
    };

    [Fact]
    public async Task Create_FutureOneTime_IsActive()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var created = await new ReminderService(db).CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(2)));

        Assert.True(created.Id > 0);
        Assert.True(created.IsActive);
        Assert.False(created.IsPaused);
    }

    [Fact]
    public async Task Create_PastOneTime_IsSavedInactive()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var created = await new ReminderService(db).CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(-2)));

        Assert.False(created.IsActive);
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    [InlineData("Monthly")]
    [InlineData("Annually")]
    [InlineData("Custom:1,3,5")]
    public async Task Create_RecurringWhoseTimeAlreadyPassed_StartsAtItsNextOccurrenceInsteadOfFiringNow(string interval)
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var created = await new ReminderService(db).CreateAsync(EmailReminder(user, DateTime.UtcNow.AddMinutes(-5), interval));

        Assert.True(created.IsActive);
        Assert.True(created.OccursAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Create_CustomDays_StartingOnAnUnselectedDay_MovesToTheFirstSelectedDay()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(9);
        var otherDay = start.AddDays(2).DayOfWeek;

        var created = await new ReminderService(db).CreateAsync(
            EmailReminder(user, start, RecurrenceCalculator.FormatCustomDays([otherDay])));

        Assert.Equal(otherDay, created.OccursAt.DayOfWeek);
        Assert.Equal(start.AddDays(2), created.OccursAt);
    }

    [Theory]
    [InlineData("Custom")]
    [InlineData("Custom:")]
    [InlineData("Fortnightly")]
    public async Task Create_RejectsIntervalsTheSchedulerCannotAdvance(string interval)
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReminderService(db).CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1), interval)));
    }

    [Fact]
    public async Task Create_EmailChannel_RequiresAConfirmedEmail()
    {
        var user = _database.AddUser(emailConfirmed: false);
        using var db = _database.NewContext();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReminderService(db).CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1))));
        Assert.Contains("confirmed email", ex.Message);
    }

    [Fact]
    public async Task Create_RejectsSomeoneElsesEmailAsTarget()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var reminder = EmailReminder(user, DateTime.UtcNow.AddHours(1));
        reminder.Targets.First().TargetValue = "victim@example.com";

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReminderService(db).CreateAsync(reminder));
    }

    [Fact]
    public async Task Create_FreePlan_StopsAtTheReminderLimit()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var service = new ReminderService(db);

        for (var i = 0; i < ReminderService.FreeReminderLimit; i++)
            await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1), name: $"Reminder {i}"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1), name: "One too many")));
    }

    [Fact]
    public async Task Create_PaidPlan_HasNoReminderLimit()
    {
        var user = _database.AddUser(tier: SubscriptionTier.Pro);
        using var db = _database.NewContext();
        var service = new ReminderService(db);

        for (var i = 0; i <= ReminderService.FreeReminderLimit; i++)
            await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1), name: $"Reminder {i}"));

        Assert.Equal(ReminderService.FreeReminderLimit + 1, await db.Reminders.CountAsync());
    }

    [Fact]
    public async Task Update_ChangesFieldsChannelsAndTargets()
    {
        var user = _database.AddUser();
        int id;
        using (var db = _database.NewContext())
            id = (await new ReminderService(db).CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1)))).Id;

        using (var db = _database.NewContext())
        {
            var edit = EmailReminder(user, DateTime.UtcNow.AddDays(1), "Weekly", "Pay rent (edited)");
            edit.Id = id;
            edit.Channels.Clear();
            edit.Targets.Clear();
            await new ReminderService(db).UpdateAsync(edit);
        }

        using (var db = _database.NewContext())
        {
            var stored = await new ReminderService(db).GetByIdAsync(id, user.Id);
            Assert.NotNull(stored);
            Assert.Equal("Pay rent (edited)", stored.Name);
            Assert.Equal("Weekly", stored.RecurringInterval);
            Assert.Empty(stored.Channels);
            Assert.Empty(stored.Targets);
        }
    }

    [Fact]
    public async Task Update_ThatFailsValidation_LeavesTheStoredReminderUntouched()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext(); // one context, like a Blazor circuit
        var service = new ReminderService(db);
        var id = (await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1)))).Id;

        var edit = EmailReminder(user, DateTime.UtcNow.AddHours(3), name: "Broken edit");
        edit.Id = id;
        edit.Targets.Clear(); // Email channel with no target is invalid

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(edit));

        // A later, unrelated save on the same context must not persist any part of the rejected edit.
        await service.PauseAsync(id, user.Id);

        using var check = _database.NewContext();
        var stored = await check.Reminders.Include(r => r.Channels).Include(r => r.Targets).SingleAsync(r => r.Id == id);
        Assert.Equal("Pay rent", stored.Name);
        Assert.Single(stored.Channels);
        Assert.Single(stored.Targets);
        Assert.True(stored.IsPaused);
    }

    [Fact]
    public async Task Update_DoesNotTouchAnotherUsersReminder()
    {
        var owner = _database.AddUser("owner@example.com");
        var other = _database.AddUser("other@example.com");
        using var db = _database.NewContext();
        var service = new ReminderService(db);
        var id = (await service.CreateAsync(EmailReminder(owner, DateTime.UtcNow.AddHours(1)))).Id;

        var edit = EmailReminder(other, DateTime.UtcNow.AddHours(1), name: "Hijacked");
        edit.Id = id;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(edit));
        Assert.Null(await service.GetByIdAsync(id, other.Id));
        Assert.False(await service.DeleteAsync(id, other.Id));
        Assert.Equal("Pay rent", (await service.GetByIdAsync(id, owner.Id))!.Name);
    }

    [Fact]
    public async Task Update_KeepsAPausedReminderPaused()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var service = new ReminderService(db);
        var id = (await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1)))).Id;
        await service.PauseAsync(id, user.Id);

        var edit = EmailReminder(user, DateTime.UtcNow.AddHours(5), name: "Renamed");
        edit.Id = id;
        await service.UpdateAsync(edit);

        Assert.True((await service.GetByIdAsync(id, user.Id))!.IsPaused);
    }

    [Fact]
    public async Task Resume_RecurringThatWasDueWhilePaused_MovesToNextOccurrence()
    {
        var user = _database.AddUser();
        int id;
        using (var db = _database.NewContext())
        {
            var service = new ReminderService(db);
            id = (await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1), "Daily"))).Id;
            await service.PauseAsync(id, user.Id);
        }

        // Time passes while it is paused.
        using (var db = _database.NewContext())
        {
            var stored = await db.Reminders.SingleAsync(r => r.Id == id);
            stored.OccursAt = DateTime.UtcNow.AddDays(-3);
            await db.SaveChangesAsync();
        }

        using (var db = _database.NewContext())
        {
            var service = new ReminderService(db);
            Assert.True(await service.ResumeAsync(id, user.Id));
            var resumed = await service.GetByIdAsync(id, user.Id);
            Assert.False(resumed!.IsPaused);
            Assert.True(resumed.IsActive);
            Assert.True(resumed.OccursAt > DateTime.UtcNow);
        }
    }

    [Fact]
    public async Task Resume_ExpiredOneTime_StaysInactive()
    {
        var user = _database.AddUser();
        using var db = _database.NewContext();
        var service = new ReminderService(db);
        var id = (await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(-1)))).Id;

        await service.ResumeAsync(id, user.Id);

        Assert.False((await service.GetByIdAsync(id, user.Id))!.IsActive);
    }

    [Fact]
    public async Task Reads_SeeChangesMadeByAnotherContext()
    {
        var user = _database.AddUser();
        using var circuit = _database.NewContext(); // long-lived, like a Blazor circuit
        var service = new ReminderService(circuit);
        var id = (await service.CreateAsync(EmailReminder(user, DateTime.UtcNow.AddHours(1)))).Id;
        Assert.Single(await service.GetUserRemindersAsync(user.Id));

        // The scheduler fires the reminder from its own context.
        using (var scheduler = _database.NewContext())
        {
            var stored = await scheduler.Reminders.SingleAsync(r => r.Id == id);
            stored.IsActive = false;
            await scheduler.SaveChangesAsync();
        }

        Assert.False((await service.GetUserRemindersAsync(user.Id)).Single().IsActive);
    }
}
