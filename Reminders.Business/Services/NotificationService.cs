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
        var notification = new ReminderNotification
        {
            ReminderId = reminder.Id,
            Channel = channel,
            Status = NotificationStatus.Pending,
            ReminderName = reminder.Name,
            MessageSnapshot = string.IsNullOrWhiteSpace(reminder.Description)
                ? reminder.Name
                : $"{reminder.Name}: {reminder.Description}",
            TimeZoneId = reminder.TimeZoneId,
            ScheduledForUtc = reminder.OccursAt,
            CreatedAt = DateTime.UtcNow
        };
        _db.ReminderNotifications.Add(notification);
        await _db.SaveChangesAsync();

        try
        {
            var withinQuota = await _subscriptionService.IsChannelWithinQuotaAsync(user, channel);
            if (!withinQuota)
            {
                _logger.LogWarning("Quota exceeded for user {UserId} channel {Channel}", user.Id, channel);
                notification.Status = NotificationStatus.Failed;
                await _db.SaveChangesAsync();
                return;
            }

            switch (channel)
            {
                case NotificationChannel.Email: await _email.SendAsync(user, reminder); break;
                case NotificationChannel.SMS: await _sms.SendAsync(user, reminder); break;
                case NotificationChannel.Voice: await _voice.SendAsync(user, reminder); break;
                case NotificationChannel.Push: await _push.SendAsync(user, reminder); break;
            }
            notification.Status = NotificationStatus.Sent;
            notification.SentAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch {Channel} notification for reminder {ReminderId}", channel, reminder.Id);
            notification.Status = NotificationStatus.Failed;
        }

        await _db.SaveChangesAsync();
    }
}
