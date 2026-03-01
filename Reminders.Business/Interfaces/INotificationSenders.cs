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
    Task SendAsync(AppUser user, Reminder reminder);
}
