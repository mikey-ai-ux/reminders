using Reminders.Models.Enums;

namespace Reminders.Models;

public class ReminderTarget
{
    public int Id { get; set; }
    public int ReminderId { get; set; }
    public NotificationChannel Channel { get; set; }

    // PushDevice | EmailAddress | PhoneNumber | Calendar
    public string TargetType { get; set; } = string.Empty;

    // endpoint / email / phone / calendar url
    public string TargetValue { get; set; } = string.Empty;

    // Optional UI label (e.g. "iPhone 15", "Work Email")
    public string? Label { get; set; }

    public Reminder? Reminder { get; set; }
}
