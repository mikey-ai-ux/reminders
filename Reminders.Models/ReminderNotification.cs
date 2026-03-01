using Reminders.Models.Enums;

namespace Reminders.Models;

public enum NotificationStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}

public class ReminderNotification
{
    public int Id { get; set; }
    public int ReminderId { get; set; }
    public NotificationChannel Channel { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;
    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Reminder? Reminder { get; set; }
}
