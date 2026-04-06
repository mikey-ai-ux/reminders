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

    // Snapshot fields for historical dashboard
    public string ReminderName { get; set; } = string.Empty;
    public string MessageSnapshot { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public string? DeviceType { get; set; }
    public DateTime ScheduledForUtc { get; set; }

    public DateTime? SentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Reminder? Reminder { get; set; }
}
