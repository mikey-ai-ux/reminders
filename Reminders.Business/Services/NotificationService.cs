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
            await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Failed, null, user);
            return;
        }

        var withinQuota = await _subscriptionService.IsChannelWithinQuotaAsync(user, channel);
        if (!withinQuota)
        {
            _logger.LogWarning("Quota exceeded for user {UserId} channel {Channel}", user.Id, channel);
            await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Failed, null, user);
            return;
        }

        try
        {
            switch (channel)
            {
                case NotificationChannel.Email:
                {
                    foreach (var emailTarget in targets)
                    {
                        var targetUser = CloneForEmail(user, emailTarget);
                        await _email.SendAsync(targetUser, reminder);
                        await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Sent, null, targetUser);
                    }
                    break;
                }
                case NotificationChannel.SMS:
                {
                    foreach (var phoneTarget in targets)
                    {
                        var targetUser = CloneForPhone(user, phoneTarget);
                        await _sms.SendAsync(targetUser, reminder);
                        await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Sent, null, targetUser);
                    }
                    break;
                }
                case NotificationChannel.Voice:
                {
                    foreach (var phoneTarget in targets)
                    {
                        var targetUser = CloneForPhone(user, phoneTarget);
                        await _voice.SendAsync(targetUser, reminder);
                        await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Sent, null, targetUser);
                    }
                    break;
                }
                case NotificationChannel.Push:
                {
                    var deviceTypes = await _db.PushSubscriptions
                        .AsNoTracking()
                        .Where(x => x.UserId == user.Id && targets.Contains(x.Endpoint))
                        .Select(x => x.DeviceType)
                        .Distinct()
                        .ToListAsync();

                    var deviceTypeSnapshot = deviceTypes.Count == 0
                        ? "Unknown"
                        : string.Join(", ", deviceTypes.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x));

                    await _push.SendAsync(user, reminder, targets);
                    await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Sent, deviceTypeSnapshot, user);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Channel} notification for reminder {ReminderId}", channel, reminder.Id);
            await RecordNotificationResultAsync(reminder, channel, NotificationStatus.Failed, null, user);
        }
    }

    private async Task RecordNotificationResultAsync(Reminder reminder, NotificationChannel channel, NotificationStatus status, string? deviceType, AppUser user)
    {
        var notification = new ReminderNotification
        {
            ReminderId = reminder.Id,
            Channel = channel,
            Status = status,
            ReminderName = reminder.Name,
            MessageSnapshot = string.IsNullOrWhiteSpace(reminder.Description)
                ? reminder.Name
                : $"{reminder.Name}: {reminder.Description}",
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
