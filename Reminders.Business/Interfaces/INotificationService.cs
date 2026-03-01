using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Business.Interfaces;

public interface INotificationService
{
    Task DispatchAsync(Reminder reminder, NotificationChannel channel, AppUser user);
}
