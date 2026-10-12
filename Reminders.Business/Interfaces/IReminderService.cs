using Reminders.Models;

namespace Reminders.Business.Interfaces;

public interface IReminderService
{
    Task<List<Reminder>> GetUserRemindersAsync(string userId);
    Task<Reminder?> GetByIdAsync(int id, string userId);
    Task<Reminder> CreateAsync(Reminder reminder);
    Task<Reminder> UpdateAsync(Reminder reminder);
    Task<bool> DeleteAsync(int id, string userId);
    Task<bool> PauseAsync(int id, string userId);
    Task<bool> ResumeAsync(int id, string userId);
    Task<List<ReminderNotification>> GetNotificationHistoryAsync(string userId);
    Task<NotificationHistoryPage> GetNotificationHistoryPageAsync(string userId, NotificationHistoryQuery query);

    /// <summary>Sends the reminder once more after <paramref name="delay"/>, leaving its normal schedule alone.</summary>
    Task<bool> SnoozeAsync(int id, string userId, TimeSpan delay);

    /// <summary>
    /// Marks a reminder as dealt with. Any pending snooze is cancelled. With <paramref name="skipUpcoming"/>
    /// (used from the list, before it fires) a one-time reminder is finished and a recurring one moves to its
    /// next occurrence; without it (used from a notification that already fired) the schedule is left alone.
    /// </summary>
    Task<bool> MarkDoneAsync(int id, string userId, bool skipUpcoming);

    /// <summary>The reminder as seen from a notification link: only what the action page needs.</summary>
    Task<Reminder?> GetForActionAsync(int id, string userId);
}
