using Reminders.Models.Enums;

namespace Reminders.Models;

public class Reminder
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Icon { get; set; } = "🔔";
    public string? CustomIconUrl { get; set; }
    public DateTime OccursAt { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;
    public bool IsPaused { get; set; } = false;
    public string RecurringInterval { get; set; } = "None"; // None, Daily, Weekly, Monthly, Annually, Custom:<day numbers>

    // Day of month the user picked for Monthly/Annually, so the 31st doesn't drift after shorter months.
    public int? RecurrenceAnchorDay { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastNotifiedAt { get; set; }

    // Set when the user snoozes a notification: one extra notification is sent at this time (UTC),
    // without changing the regular schedule.
    public DateTime? SnoozedUntil { get; set; }

    public AppUser? User { get; set; }
    public ICollection<ReminderNotification> Notifications { get; set; } = new List<ReminderNotification>();
    public ICollection<ReminderChannel> Channels { get; set; } = new List<ReminderChannel>();
    public ICollection<ReminderTarget> Targets { get; set; } = new List<ReminderTarget>();
}

public class ReminderChannel
{
    public int Id { get; set; }
    public int ReminderId { get; set; }
    public NotificationChannel Channel { get; set; }
    public Reminder? Reminder { get; set; }
}
