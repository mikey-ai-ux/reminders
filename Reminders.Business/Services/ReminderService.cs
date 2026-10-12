using Microsoft.EntityFrameworkCore;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;

namespace Reminders.Business.Services;

public class ReminderService : IReminderService
{
    public const int FreeReminderLimit = 10;

    private readonly AppDbContext _db;

    public ReminderService(AppDbContext db) => _db = db;

    // Reads are untracked: the scheduler changes reminders from another DbContext, and a tracked
    // copy held by a long-lived Blazor circuit would keep showing (and saving) stale values.
    public async Task<List<Reminder>> GetUserRemindersAsync(string userId) =>
        await _db.Reminders
            .AsNoTracking()
            .Include(r => r.Channels)
            .Include(r => r.Targets)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .AsSplitQuery()
            .ToListAsync();

    public async Task<Reminder?> GetByIdAsync(int id, string userId) =>
        await _db.Reminders
            .AsNoTracking()
            .Include(r => r.Channels)
            .Include(r => r.Targets)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

    public async Task<Reminder> CreateAsync(Reminder reminder)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == reminder.UserId);
        if (user != null && user.SubscriptionTier == Reminders.Models.Enums.SubscriptionTier.Free)
        {
            // Finished one-time reminders stay in the list as history; only ones that can still fire count.
            var currentCount = await _db.Reminders.CountAsync(r => r.UserId == reminder.UserId && r.IsActive);
            if (currentCount >= FreeReminderLimit)
                throw new InvalidOperationException($"Free plan allows up to {FreeReminderLimit} active reminders. Delete or finish one, or upgrade to a paid plan for unlimited reminders.");
        }

        Normalize(reminder);
        await ValidateChannelsAndTargetsAsync(reminder, user);

        var now = DateTime.UtcNow;
        reminder.Id = 0;
        reminder.IsPaused = false;
        reminder.LastNotifiedAt = null;
        reminder.SnoozedUntil = null;
        ApplySchedule(reminder, now);
        reminder.CreatedAt = now;
        reminder.UpdatedAt = now;
        _db.Reminders.Add(reminder);
        await _db.SaveChangesAsync();
        return reminder;
    }

    public async Task<Reminder> UpdateAsync(Reminder reminder)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == reminder.UserId);

        // Validate before touching the stored row, so a rejected edit leaves nothing half-applied.
        Normalize(reminder);
        await ValidateChannelsAndTargetsAsync(reminder, user);

        var existing = await LoadFreshAsync(reminder.Id, reminder.UserId)
            ?? throw new InvalidOperationException("Reminder not found.");

        var now = DateTime.UtcNow;
        existing.Name = reminder.Name;
        existing.Description = reminder.Description;
        existing.Icon = reminder.Icon;
        existing.CustomIconUrl = reminder.CustomIconUrl;
        existing.OccursAt = reminder.OccursAt;
        existing.TimeZoneId = reminder.TimeZoneId;
        existing.RecurringInterval = reminder.RecurringInterval;
        existing.RecurrenceAnchorDay = reminder.RecurrenceAnchorDay;
        existing.SnoozedUntil = null; // rescheduling replaces any pending snooze
        ApplySchedule(existing, now);
        existing.UpdatedAt = now;

        _db.ReminderChannels.RemoveRange(await _db.ReminderChannels.Where(c => c.ReminderId == existing.Id).ToListAsync());
        _db.ReminderTargets.RemoveRange(await _db.ReminderTargets.Where(t => t.ReminderId == existing.Id).ToListAsync());

        foreach (var c in reminder.Channels)
            _db.ReminderChannels.Add(new ReminderChannel { ReminderId = existing.Id, Channel = c.Channel });
        foreach (var t in reminder.Targets)
            _db.ReminderTargets.Add(new ReminderTarget { ReminderId = existing.Id, Channel = t.Channel, TargetType = t.TargetType, TargetValue = t.TargetValue, Label = t.Label });

        await _db.SaveChangesAsync();
        return existing;
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
        var r = await LoadFreshAsync(id, userId);
        if (r == null) return false;
        r.IsPaused = true;
        r.SnoozedUntil = null;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ResumeAsync(int id, string userId)
    {
        var r = await LoadFreshAsync(id, userId);
        if (r == null) return false;
        r.IsPaused = false;
        ApplySchedule(r, DateTime.UtcNow);
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public static readonly TimeSpan MinSnooze = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxSnooze = TimeSpan.FromDays(7);

    public async Task<bool> SnoozeAsync(int id, string userId, TimeSpan delay)
    {
        var r = await LoadFreshAsync(id, userId);
        if (r == null) return false;

        if (delay < MinSnooze) delay = MinSnooze;
        if (delay > MaxSnooze) delay = MaxSnooze;

        r.SnoozedUntil = DateTime.UtcNow.Add(delay);
        r.IsPaused = false; // asking to be reminded again overrides a pause
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MarkDoneAsync(int id, string userId, bool skipUpcoming)
    {
        var r = await LoadFreshAsync(id, userId);
        if (r == null) return false;

        var now = DateTime.UtcNow;
        r.SnoozedUntil = null;

        if (!RecurrenceCalculator.IsRecurring(r.RecurringInterval))
        {
            r.IsActive = false;
        }
        else if (skipUpcoming && r.IsActive)
        {
            // Done early: skip the occurrence that was coming up.
            var after = r.OccursAt > now ? r.OccursAt : now;
            var next = RecurrenceCalculator.GetNextOccurrenceUtc(r.OccursAt, r.RecurringInterval, r.TimeZoneId, after, r.RecurrenceAnchorDay);
            if (next.HasValue) r.OccursAt = next.Value;
            else r.IsActive = false;
        }

        r.UpdatedAt = now;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Reminder?> GetForActionAsync(int id, string userId) =>
        await _db.Reminders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);

    public async Task<NotificationHistoryPage> GetNotificationHistoryPageAsync(string userId, NotificationHistoryQuery query)
    {
        var pageSize = Math.Clamp(query.PageSize, 1, NotificationHistoryQuery.MaxPageSize);

        var rows = _db.ReminderNotifications
            .AsNoTracking()
            .Where(n => n.Reminder != null && n.Reminder.UserId == userId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // LIKE ignores case on both SQL Server and SQLite (Contains does not on SQLite).
            // Escape the wildcard characters so the text is matched literally.
            var term = query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
            var pattern = $"%{term}%";
            rows = rows.Where(n => EF.Functions.Like(n.ReminderName, pattern, "\\") || EF.Functions.Like(n.MessageSnapshot, pattern, "\\"));
        }
        if (query.Channel.HasValue) rows = rows.Where(n => n.Channel == query.Channel.Value);
        if (query.Status.HasValue) rows = rows.Where(n => n.Status == query.Status.Value);

        var total = await rows.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Clamp(query.Page, 1, totalPages);

        // Id breaks ties so rows never jump between pages.
        rows = query.Sort switch
        {
            "sent_asc" => rows.OrderBy(n => n.SentAt ?? n.CreatedAt).ThenBy(n => n.Id),
            "channel_asc" => rows.OrderBy(n => n.Channel).ThenByDescending(n => n.SentAt ?? n.CreatedAt).ThenByDescending(n => n.Id),
            "status_asc" => rows.OrderBy(n => n.Status).ThenByDescending(n => n.SentAt ?? n.CreatedAt).ThenByDescending(n => n.Id),
            _ => rows.OrderByDescending(n => n.SentAt ?? n.CreatedAt).ThenByDescending(n => n.Id)
        };

        var items = await rows.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new NotificationHistoryPage(items, total, page, pageSize);
    }

    private async Task<Reminder?> LoadFreshAsync(int id, string userId)
    {
        var r = await _db.Reminders.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        // The context may already track this row with values the scheduler has since changed.
        if (r != null) await _db.Entry(r).ReloadAsync();
        return r;
    }

    /// <summary>
    /// Decides whether the reminder is live. A recurring reminder whose time has passed moves to its
    /// next occurrence instead of firing straight away; a one-time reminder in the past is inactive.
    /// </summary>
    private static void ApplySchedule(Reminder reminder, DateTime now)
    {
        var after = now;
        if (reminder.OccursAt > now)
        {
            // A custom-days reminder whose start date isn't one of its days begins on the next matching day.
            var days = RecurrenceCalculator.ParseCustomDays(reminder.RecurringInterval);
            var localDay = TimeZoneInfo.ConvertTimeFromUtc(reminder.OccursAt, RecurrenceCalculator.ResolveTimeZone(reminder.TimeZoneId)).DayOfWeek;
            if (days.Count == 0 || days.Contains(localDay))
            {
                reminder.IsActive = true;
                return;
            }
            after = reminder.OccursAt;
        }

        var next = RecurrenceCalculator.GetNextOccurrenceUtc(
            reminder.OccursAt, reminder.RecurringInterval, reminder.TimeZoneId, after, reminder.RecurrenceAnchorDay);

        if (next.HasValue)
        {
            reminder.OccursAt = next.Value;
            reminder.IsActive = true;
        }
        else
        {
            reminder.IsActive = false;
        }
    }

    private static void Normalize(Reminder reminder)
    {
        reminder.Name = (reminder.Name ?? string.Empty).Trim();
        if (reminder.Name.Length == 0)
            throw new InvalidOperationException("Reminder name is required.");
        if (reminder.Name.Length > 200)
            throw new InvalidOperationException("Reminder name must be 200 characters or fewer.");

        reminder.Description = string.IsNullOrWhiteSpace(reminder.Description) ? null : reminder.Description.Trim();
        reminder.Icon = string.IsNullOrWhiteSpace(reminder.Icon) ? "bell" : reminder.Icon;
        if (reminder.Icon.Length > 20)
            throw new InvalidOperationException("Icon is too long.");

        reminder.CustomIconUrl = string.IsNullOrWhiteSpace(reminder.CustomIconUrl) ? null : reminder.CustomIconUrl.Trim();
        if (reminder.CustomIconUrl != null)
        {
            var ok = reminder.CustomIconUrl.Length <= 1024
                     && Uri.TryCreate(reminder.CustomIconUrl, UriKind.Absolute, out var uri)
                     && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            if (!ok)
                throw new InvalidOperationException("Custom icon URL must be a valid absolute http/https URL.");
        }

        reminder.TimeZoneId = string.IsNullOrWhiteSpace(reminder.TimeZoneId) ? "UTC" : reminder.TimeZoneId;
        try { TimeZoneInfo.FindSystemTimeZoneById(reminder.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException("Unknown timezone.");
        }

        reminder.RecurringInterval = string.IsNullOrWhiteSpace(reminder.RecurringInterval) ? RecurrenceCalculator.None : reminder.RecurringInterval;
        if (reminder.RecurringInterval.StartsWith(RecurrenceCalculator.CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var days = RecurrenceCalculator.ParseCustomDays(reminder.RecurringInterval);
            if (days.Count == 0)
                throw new InvalidOperationException("Pick at least one day for a custom-days reminder.");
            reminder.RecurringInterval = RecurrenceCalculator.FormatCustomDays(days);
        }
        else if (!RecurrenceCalculator.IsSupported(reminder.RecurringInterval))
        {
            throw new InvalidOperationException("Unknown recurring interval.");
        }

        if (reminder.OccursAt == default)
            throw new InvalidOperationException("Date and time are required.");
        reminder.OccursAt = DateTime.SpecifyKind(reminder.OccursAt, DateTimeKind.Utc);

        if (reminder.RecurringInterval is RecurrenceCalculator.Monthly or RecurrenceCalculator.Annually)
        {
            reminder.RecurrenceAnchorDay ??= TimeZoneInfo
                .ConvertTimeFromUtc(reminder.OccursAt, RecurrenceCalculator.ResolveTimeZone(reminder.TimeZoneId)).Day;
        }
        else
        {
            reminder.RecurrenceAnchorDay = null;
        }

        reminder.Channels = reminder.Channels
            .GroupBy(c => c.Channel)
            .Select(g => new ReminderChannel { Channel = g.Key })
            .ToList();

        reminder.Targets = reminder.Targets
            .Where(t => !string.IsNullOrWhiteSpace(t.TargetValue))
            .GroupBy(t => (t.Channel, Value: t.TargetValue.Trim().ToLowerInvariant()))
            .Select(g => g.First())
            .Select(t => new ReminderTarget { Channel = t.Channel, TargetType = t.TargetType, TargetValue = t.TargetValue.Trim(), Label = t.Label })
            .ToList();
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
