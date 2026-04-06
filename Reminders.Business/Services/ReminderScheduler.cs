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
                await ProcessDueRemindersAsync(stoppingToken);
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
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("ReminderScheduler stopped");
    }

    private async Task ProcessDueRemindersAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        var now = DateTime.UtcNow;
        var recentCutoff = now.AddSeconds(-55);

        var dueReminders = await db.Reminders
            .Include(r => r.Channels)
            .Include(r => r.Targets)
            .Include(r => r.User)
            .Where(r => r.IsActive
                && !r.IsPaused
                && r.OccursAt <= now
                && (r.LastNotifiedAt == null || r.LastNotifiedAt < recentCutoff))
            .ToListAsync(ct);

        if (!dueReminders.Any()) return;

        _logger.LogInformation("Found {Count} due reminders to process", dueReminders.Count);
        int sent = 0, failed = 0;

        foreach (var reminder in dueReminders)
        {
            if (reminder.User == null) continue;

            foreach (var channelEntry in reminder.Channels)
            {
                if (!subscriptionService.IsChannelAllowed(reminder.User, channelEntry.Channel))
                {
                    _logger.LogDebug("Channel {Channel} not allowed for user {UserId} (tier: {Tier})",
                        channelEntry.Channel, reminder.User.Id, reminder.User.SubscriptionTier);
                    continue;
                }

                try
                {
                    await notificationService.DispatchAsync(reminder, channelEntry.Channel, reminder.User);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to dispatch {Channel} for reminder {ReminderId}", channelEntry.Channel, reminder.Id);
                    failed++;
                }
            }

            reminder.LastNotifiedAt = now;

            if (reminder.RecurringInterval == "None")
            {
                reminder.IsActive = false;
            }
            else
            {
                reminder.OccursAt = reminder.RecurringInterval switch
                {
                    "Daily" => reminder.OccursAt.AddDays(1),
                    "Weekly" => reminder.OccursAt.AddDays(7),
                    "Monthly" => reminder.OccursAt.AddMonths(1),
                    _ => reminder.OccursAt
                };
            }

            reminder.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Reminder processing complete: {Sent} sent, {Failed} failed", sent, failed);
    }
}
