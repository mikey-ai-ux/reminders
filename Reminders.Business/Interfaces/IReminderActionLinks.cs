using Reminders.Models;

namespace Reminders.Business.Interfaces;

/// <summary>
/// Builds the "snooze" and "mark done" links carried by a notification. The links hold a signed,
/// expiring token, so they work from an email or a push notification without the user being signed in.
/// </summary>
public interface IReminderActionLinks
{
    /// <summary>Site-relative path of the page offering snooze and done buttons.</summary>
    string ActionPagePath(Reminder reminder);

    /// <summary>Site-relative path to POST to in order to snooze.</summary>
    string SnoozePath(Reminder reminder, int minutes);

    /// <summary>Site-relative path to POST to in order to mark the reminder done.</summary>
    string DonePath(Reminder reminder);

    /// <summary>Turns a site-relative path into a full URL, or null when the site address is not configured.</summary>
    string? Absolute(string path);
}