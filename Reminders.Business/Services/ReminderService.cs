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

        // If reminder is scheduled in the future, keep it active.
        if (reminder.OccursAt > DateTime.UtcNow)
            reminder.IsActive = true;

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
        if (r.OccursAt > DateTime.UtcNow)
            r.IsActive = true;
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

        var confirmedEndpoints = await _db.UserContactEndpoints
            .AsNoTracking()
            .Where(x => x.UserId == reminder.UserId && x.IsConfirmed)
            .ToListAsync();

        // Email requires at least one confirmed email endpoint and selected targets must belong to confirmed set.
        if (selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.Email))
        {
            var allowedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(user.Email) && user.EmailConfirmed)
                allowedEmails.Add(user.Email);

            foreach (var ep in confirmedEndpoints.Where(x => x.Channel == Reminders.Models.Enums.NotificationChannel.Email))
                allowedEmails.Add(ep.Value);

            if (allowedEmails.Count == 0)
                throw new InvalidOperationException("Email channel requires a registered and confirmed email.");

            var emailTargets = reminder.Targets
                .Where(t => t.Channel == Reminders.Models.Enums.NotificationChannel.Email)
                .Select(t => t.TargetValue)
                .ToList();

            if (emailTargets.Count == 0)
                throw new InvalidOperationException("Select at least one email target for Email channel.");

            if (emailTargets.Any(t => !allowedEmails.Contains(t)))
                throw new InvalidOperationException("Selected email target is not valid/confirmed for current user.");
        }

        // SMS/Voice require at least one confirmed phone endpoint and selected targets must belong to confirmed set.
        var hasPhoneChannel = selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.SMS)
                              || selectedChannels.Contains(Reminders.Models.Enums.NotificationChannel.Voice);

        if (hasPhoneChannel)
        {
            var allowedPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(user.PhoneNumber) && user.PhoneNumberConfirmed)
                allowedPhones.Add(user.PhoneNumber);

            foreach (var ep in confirmedEndpoints.Where(x => x.Channel == Reminders.Models.Enums.NotificationChannel.SMS || x.Channel == Reminders.Models.Enums.NotificationChannel.Voice))
                allowedPhones.Add(ep.Value);

            if (allowedPhones.Count == 0)
                throw new InvalidOperationException("SMS/Voice channels require a registered and confirmed phone number.");

            var phoneTargets = reminder.Targets
                .Where(t => t.Channel == Reminders.Models.Enums.NotificationChannel.SMS
                         || t.Channel == Reminders.Models.Enums.NotificationChannel.Voice)
                .Select(t => t.TargetValue)
                .ToList();

            if (phoneTargets.Count == 0)
                throw new InvalidOperationException("Select at least one phone target for SMS/Voice channels.");

            if (phoneTargets.Any(t => !allowedPhones.Contains(t)))
                throw new InvalidOperationException("Selected phone target is not valid/confirmed for current user.");
        }

        // Ensure targets are provided only for selected channels.
        var invalidTargets = reminder.Targets.Where(t => !selectedChannels.Contains(t.Channel)).ToList();
        if (invalidTargets.Count > 0)
            throw new InvalidOperationException("Found targets for channels that are not selected.");
    }
}
