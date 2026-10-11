using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Business.Services;

public class NotificationService : INotificationService
{
    private readonly IEmailNotificationSender _email;
    private readonly ISmsNotificationSender _sms;
    private readonly IVoiceNotificationSender _voice;
    private readonly IPushNotificationSender _push;
    private readonly AppDbContext _db;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IEmailNotificationSender email,
        ISmsNotificationSender sms,
        IVoiceNotificationSender voice,
        IPushNotificationSender push,
        AppDbContext db,
        ISubscriptionService subscriptionService,
        ILogger<NotificationService> logger)
    {
        _email = email; _sms = sms; _voice = voice; _push = push;
        _db = db;
        _subscriptionService = subscriptionService;
        _logger = logger;
    }

    public async Task DispatchAsync(Reminder reminder, NotificationChannel channel, AppUser user)
    {
        // Channel-specific selected targets from reminder configuration
        var targets = reminder.Targets
            .Where(t => t.Channel == channel)
            .Select(t => t.TargetValue)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            _logger.LogWarning("No selected targets for reminder {ReminderId} channel {Channel}", reminder.Id, channel);
            await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Failed, null);
            return;
        }

        if (channel == NotificationChannel.Push)
        {
            var deviceTypes = await _db.PushSubscriptions
                .AsNoTracking()
                .Where(x => x.UserId == user.Id && targets.Contains(x.Endpoint))
                .Select(x => x.DeviceType)
                .Distinct()
                .ToListAsync();

            var named = deviceTypes.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x).ToList();
            var deviceTypeSnapshot = named.Count == 0 ? "Unknown" : string.Join(", ", named);
            if (deviceTypeSnapshot.Length > 80) deviceTypeSnapshot = deviceTypeSnapshot[..80];

            await SendOneAsync(reminder, channel, deviceTypeSnapshot, async () =>
            {
                var delivered = await _push.SendAsync(user, reminder, targets);
                if (delivered == 0)
                    throw new InvalidOperationException("Push could not be delivered to any selected device.");
            });
            return;
        }

        // One record per target, and one failing target must not stop the others.
        foreach (var target in targets)
        {
            // Re-check per target so a reminder with several phone numbers can't overshoot the monthly quota.
            if (channel is NotificationChannel.SMS or NotificationChannel.Voice
                && !await _subscriptionService.IsChannelWithinQuotaAsync(user, channel))
            {
                _logger.LogWarning("Quota exceeded for user {UserId} channel {Channel}", user.Id, channel);
                await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Failed, null);
                continue;
            }

            await SendOneAsync(reminder, channel, null, () => channel switch
            {
                NotificationChannel.Email => _email.SendAsync(CloneForEmail(user, target), reminder),
                NotificationChannel.SMS => _sms.SendAsync(CloneForPhone(user, target), reminder),
                NotificationChannel.Voice => _voice.SendAsync(CloneForPhone(user, target), reminder),
                _ => Task.CompletedTask
            });
        }
    }

    private async Task SendOneAsync(Reminder reminder, NotificationChannel channel, string? deviceType, Func<Task> send)
    {
        try
        {
            await send();
            await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Sent, deviceType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Channel} notification for reminder {ReminderId}", channel, reminder.Id);
            await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Failed, deviceType);
        }
    }

    private async Task RecordNotificationResultAsync(Reminder reminder, NotificationChannel channel, NotificationStatus status, string? deviceType)
    {
        var snapshot = string.IsNullOrWhiteSpace(reminder.Description)
            ? reminder.Name
            : $"{reminder.Name}: {reminder.Description}";
        if (snapshot.Length > 2000) snapshot = snapshot[..2000];

        var notification = new ReminderNotification
        {
            ReminderId = reminder.Id,
            Channel = channel,
            Status = status,
            ReminderName = reminder.Name,
            MessageSnapshot = snapshot,
            TimeZoneId = reminder.TimeZoneId,
            DeviceType = deviceType,
            ScheduledForUtc = reminder.OccursAt,
            SentAt = status == NotificationStatus.Sent ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        };

        _db.ReminderNotifications.Add(notification);
        await _db.SaveChangesAsync();
    }

    private static AppUser CloneForEmail(AppUser source, string email)
    {
        return new AppUser
        {
            Id = source.Id,
            Email = email,
            DisplayName = source.DisplayName,
            PhoneNumber = source.PhoneNumber,
            TimeZoneId = source.TimeZoneId
        };
    }

    private static AppUser CloneForPhone(AppUser source, string phone)
    {
        return new AppUser
        {
            Id = source.Id,
            Email = source.Email,
            DisplayName = source.DisplayName,
            PhoneNumber = phone,
            TimeZoneId = source.TimeZoneId
        };
    }
}
