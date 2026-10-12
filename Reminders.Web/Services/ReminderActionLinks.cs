using Microsoft.AspNetCore.DataProtection;
using Reminders.Business.Interfaces;
using Reminders.Models;

namespace Reminders.Web.Services;

/// <summary>
/// Signs and reads the tokens used by notification links (snooze / mark done). A token names one
/// reminder and its owner, is tamper-proof, and stops working after <see cref="Lifetime"/>.
/// </summary>
public class ReminderActionLinks : IReminderActionLinks
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly ITimeLimitedDataProtector _protector;
    private readonly string? _baseUrl;

    public ReminderActionLinks(IDataProtectionProvider provider, IConfiguration config)
    {
        _protector = provider.CreateProtector("Reminders.NotificationActions.v1").ToTimeLimitedDataProtector();
        var configured = config["AppBaseUrl"];
        _baseUrl = Uri.TryCreate(configured, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? configured!.TrimEnd('/')
            : null;
    }

    public string CreateToken(Reminder reminder) => _protector.Protect($"{reminder.Id}|{reminder.UserId}", Lifetime);

    public bool TryReadToken(string? token, out int reminderId, out string userId)
    {
        reminderId = 0;
        userId = string.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var parts = _protector.Unprotect(token).Split('|', 2);
            if (parts.Length != 2 || !int.TryParse(parts[0], out reminderId) || parts[1].Length == 0) return false;
            userId = parts[1];
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false; // forged, damaged or expired
        }
    }

    public string ActionPagePath(Reminder reminder) => $"/r/{CreateToken(reminder)}";
    public string SnoozePath(Reminder reminder, int minutes) => $"/api/reminder-actions/{CreateToken(reminder)}/snooze?minutes={minutes}";
    public string DonePath(Reminder reminder) => $"/api/reminder-actions/{CreateToken(reminder)}/done";
    public string? Absolute(string path) => _baseUrl is null ? null : _baseUrl + path;
}