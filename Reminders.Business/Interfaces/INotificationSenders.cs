using Reminders.Models;

namespace Reminders.Business.Interfaces;

public interface IEmailNotificationSender
{
    Task SendAsync(AppUser user, Reminder reminder);
}

public interface ISmsNotificationSender
{
    Task SendAsync(AppUser user, Reminder reminder);
}

public interface IVoiceNotificationSender
{
    Task SendAsync(AppUser user, Reminder reminder);
}

public interface IPushNotificationSender
{
    /// <summary>Returns how many devices the notification was delivered to.</summary>
    Task<int> SendAsync(AppUser user, Reminder reminder, IEnumerable<string>? endpointFilter = null);
}
