using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;

namespace Reminders.Business.Services;

public class ReminderScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly ILogger<ReminderScheduler> _logger;

    public ReminderScheduler(IServiceProvider services, ILogger<ReminderScheduler> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderScheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueRemindersAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing due reminders");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("ReminderScheduler stopped");
    }

    public async Task ProcessDueRemindersAsync(DateTime now, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        var dueReminders = await db.Reminders
            .Include(r => r.Channels)
            .Include(r => r.Targets)
            .Include(r => r.User)
            .Where(r => !r.IsPaused
                && ((r.IsActive && r.OccursAt <= now) || (r.SnoozedUntil != null && r.SnoozedUntil <= now)))
            .AsSplitQuery()
            .ToListAsync(ct);

        if (dueReminders.Count == 0) return;

        _logger.LogInformation("Found {Count} due reminders to process", dueReminders.Count);
        int sent = 0, failed = 0;

        foreach (var reminder in dueReminders)
        {
            if (reminder.User == null) continue;

            foreach (var channel in reminder.Channels.Select(c => c.Channel).Distinct())
            {
                if (!subscriptionService.IsChannelAllowed(reminder.User, channel))
                {
                    _logger.LogDebug("Channel {Channel} not allowed for user {UserId} (tier: {Tier})",
                        channel, reminder.User.Id, reminder.User.SubscriptionTier);
                    continue;
                }

                try
                {
                    await notificationService.DispatchAsync(reminder, channel, reminder.User);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch {Channel} for reminder {ReminderId}", channel, reminder.Id);
                    failed++;
                }
            }

            reminder.LastNotifiedAt = now;

            // A snooze is a one-off repeat: it fires once and leaves the regular schedule alone.
            if (reminder.SnoozedUntil != null && reminder.SnoozedUntil <= now)
                reminder.SnoozedUntil = null;

            if (reminder.IsActive && reminder.OccursAt <= now)
            {
                // Jump straight to the next future occurrence: a reminder that was due while the app
                // was down fires once, not once per missed occurrence.
                var next = RecurrenceCalculator.GetNextOccurrenceUtc(
                    reminder.OccursAt, reminder.RecurringInterval, reminder.TimeZoneId, now, reminder.RecurrenceAnchorDay);

                if (next.HasValue)
                {
                    reminder.OccursAt = next.Value;
                }
                else
                {
                    if (RecurrenceCalculator.IsRecurring(reminder.RecurringInterval))
                        _logger.LogWarning("Reminder {ReminderId} has unsupported interval '{Interval}'; deactivating", reminder.Id, reminder.RecurringInterval);
                    reminder.IsActive = false;
                }
            }

            reminder.UpdatedAt = now;

            // Save per reminder so one bad row can't cause the rest to be sent again on the next pass.
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Reminder processing complete: {Sent} dispatched, {Failed} failed", sent, failed);
    }
}
