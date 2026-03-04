using Microsoft.EntityFrameworkCore;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;

namespace Reminders.Business.Services;

public class ReminderService : IReminderService
{
    private readonly AppDbContext _db;

    public ReminderService(AppDbContext db) => _db = db;

    public async Task<List<Reminder>> GetUserRemindersAsync(string userId) =>
        await _db.Reminders
            .Include(r => r.Channels)
            .Include(r => r.Notifications)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

    public async Task<Reminder?> GetByIdAsync(int id, string userId) =>
        await _db.Reminders
            .Include(r => r.Channels)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

    public async Task<Reminder> CreateAsync(Reminder reminder)
    {
        reminder.CreatedAt = DateTime.UtcNow;
        reminder.UpdatedAt = DateTime.UtcNow;
        _db.Reminders.Add(reminder);
        await _db.SaveChangesAsync();
        return reminder;
    }

    public async Task<Reminder> UpdateAsync(Reminder reminder)
    {
        reminder.UpdatedAt = DateTime.UtcNow;
        _db.Reminders.Update(reminder);
        await _db.SaveChangesAsync();
        return reminder;
    }

    public async Task<bool> DeleteAsync(int id, string userId)
    {
        var r = await _db.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (r == null) return false;
        _db.Reminders.Remove(r);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> PauseAsync(int id, string userId)
    {
        var r = await _db.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (r == null) return false;
        r.IsPaused = true;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResumeAsync(int id, string userId)
    {
        var r = await _db.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (r == null) return false;
        r.IsPaused = false;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<ReminderNotification>> GetNotificationHistoryAsync(string userId)
    {
        return await _db.ReminderNotifications
            .AsNoTracking()
            .Include(n => n.Reminder)
            .Where(n => n.Reminder != null && n.Reminder.UserId == userId)
            .OrderByDescending(n => n.SentAt ?? n.CreatedAt)
            .ToListAsync();
    }
}
