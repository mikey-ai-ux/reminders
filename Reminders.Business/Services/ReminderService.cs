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
            .Include(r => r.Targets)
            .Include(r => r.Notifications)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

    public async Task<Reminder?> GetByIdAsync(int id, string userId) =>
        await _db.Reminders
            .Include(r => r.Channels)
            .Include(r => r.Targets)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

    public async Task<Reminder> CreateAsync(Reminder reminder)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == reminder.UserId);
        if (user != null && user.SubscriptionTier == Reminders.Models.Enums.SubscriptionTier.Free)
        {
            var currentCount = await _db.Reminders.CountAsync(r => r.UserId == reminder.UserId);
            if (currentCount >= 10)
                throw new InvalidOperationException("Free plan allows up to 10 reminders. Upgrade to Pro for unlimited reminders.");
        }

        await ValidateChannelsAndTargetsAsync(reminder, user);

        reminder.CreatedAt = DateTime.UtcNow;
        reminder.UpdatedAt = DateTime.UtcNow;
        _db.Reminders.Add(reminder);
        await _db.SaveChangesAsync();
        return reminder;
    }

    public async Task<Reminder> UpdateAsync(Reminder reminder)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == reminder.UserId);
        await ValidateChannelsAndTargetsAsync(reminder, user);

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

    private async Task ValidateChannelsAndTargetsAsync(Reminder reminder, AppUser? user)
    {
        if (user == null)
            throw new InvalidOperationException("User not found for reminder validation.");

        var selectedChannels = reminder.Channels.Select(c => c.Channel).Distinct().ToHashSet();

        // Push requires at least one registered device and at least one selected push target.
        if (selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.Push))
        {
            var registeredPush = await _db.PushSubscriptions
                .AsNoTracking()
                .Where(x => x.UserId == reminder.UserId)
                .Select(x => x.Endpoint)
                .ToListAsync();

            if (registeredPush.Count == 0)
                throw new InvalidOperationException("Push channel requires at least one registered device.");

            var pushTargets = reminder.Targets
                .Where(t => t.Channel == Reminders.Models.Enums.NotificationChannel.Push)
                .Select(t => t.TargetValue)
                .ToList();

            if (pushTargets.Count == 0)
                throw new InvalidOperationException("Select at least one push device target for Push channel.");

            if (pushTargets.Any(t => !registeredPush.Contains(t)))
                throw new InvalidOperationException("One or more selected push targets are not registered for this user.");
        }

        // Email requires confirmed email + selected target that matches user's email.
        if (selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.Email))
        {
            if (string.IsNullOrWhiteSpace(user.Email) || !user.EmailConfirmed)
                throw new InvalidOperationException("Email channel requires a registered and confirmed email.");

            var emailTargets = reminder.Targets
                .Where(t => t.Channel == Reminders.Models.Enums.NotificationChannel.Email)
                .Select(t => t.TargetValue)
                .ToList();

            if (emailTargets.Count == 0)
                throw new InvalidOperationException("Select at least one email target for Email channel.");

            if (emailTargets.Any(t => !string.Equals(t, user.Email, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Selected email target is not valid for current user.");
        }

        // SMS/Voice require confirmed phone + selected target that matches user's phone.
        var hasPhoneChannel = selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.SMS)
                              || selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.Voice);

        if (hasPhoneChannel)
        {
            if (string.IsNullOrWhiteSpace(user.PhoneNumber) || !user.PhoneNumberConfirmed)
                throw new InvalidOperationException("SMS/Voice channels require a registered and confirmed phone number.");

            var phoneTargets = reminder.Targets
                .Where(t => t.Channel == Reminders.Models.Enums.NotificationChannel.SMS
                         || t.Channel == Reminders.Models.Enums.NotificationChannel.Voice)
                .Select(t => t.TargetValue)
                .ToList();

            if (phoneTargets.Count == 0)
                throw new InvalidOperationException("Select at least one phone target for SMS/Voice channels.");

            if (phoneTargets.Any(t => !string.Equals(t, user.PhoneNumber, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Selected phone target is not valid for current user.");
        }

        // Ensure targets are provided only for selected channels.
        var invalidTargets = reminder.Targets.Where(t => !selectedChannels.Contains(t.Channel)).ToList();
        if (invalidTargets.Count > 0)
            throw new InvalidOperationException("Found targets for channels that are not selected.");
    }
}
