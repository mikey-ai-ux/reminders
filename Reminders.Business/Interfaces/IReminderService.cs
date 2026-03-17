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
}
