using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Reminders.Business.Interfaces;
using Reminders.Models;

namespace Reminders.Web.Controllers;

[ApiController]
[Route("api/calendar")]
public class CalendarController : ControllerBase
{
    private readonly IReminderService _reminderService;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(IReminderService reminderService, UserManager<AppUser> userManager, ILogger<CalendarController> logger)
    {
        _reminderService = reminderService;
        _userManager = userManager;
        _logger = logger;
    }

    // GET /api/calendar/{userId}/reminders.ics
    // Public URL (authenticated via userId — so calendar apps can subscribe without cookie auth)
    [HttpGet("{userId}/reminders.ics")]
    public async Task<IActionResult> GetCalendarFeed(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user == null) return NotFound();

        var reminders = await _reminderService.GetUserRemindersAsync(userId);
        var activeReminders = reminders.Where(r => r.IsActive && !r.IsPaused).ToList();

        var ics = BuildIcsContent(activeReminders, user);
        return Content(ics, "text/calendar; charset=utf-8");
    }

    private string BuildIcsContent(List<Reminder> reminders, AppUser user)
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;

        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//Reminders App//EN");
        sb.AppendLine("CALSCALE:GREGORIAN");
        sb.AppendLine("METHOD:PUBLISH");
        sb.AppendLine("X-WR-CALNAME:My Reminders");
        sb.AppendLine("X-WR-TIMEZONE:UTC");
        sb.AppendLine("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        sb.AppendLine("X-PUBLISHED-TTL:PT1H");

        foreach (var reminder in reminders)
        {
            var uid = $"{reminder.Id}@reminders-app";
            var dtStart = reminder.OccursAt.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
            var dtEnd = reminder.OccursAt.ToUniversalTime().AddMinutes(30).ToString("yyyyMMdd'T'HHmmss'Z'");
            var dtStamp = now.ToString("yyyyMMdd'T'HHmmss'Z'");
            var summary = EscapeIcs(reminder.Name);
            var description = EscapeIcs(reminder.Description ?? "");

            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine($"UID:{uid}");
            sb.AppendLine($"DTSTAMP:{dtStamp}");
            sb.AppendLine($"DTSTART:{dtStart}");
            sb.AppendLine($"DTEND:{dtEnd}");
            sb.AppendLine($"SUMMARY:{summary}");
            if (!string.IsNullOrEmpty(description))
                sb.AppendLine($"DESCRIPTION:{description}");

            sb.AppendLine("BEGIN:VALARM");
            sb.AppendLine("TRIGGER:-PT0M");
            sb.AppendLine("ACTION:DISPLAY");
            sb.AppendLine($"DESCRIPTION:{summary}");
            sb.AppendLine("END:VALARM");

            // Handle recurrence via RecurringInterval string
            if (reminder.RecurringInterval == "Daily")
                sb.AppendLine("RRULE:FREQ=DAILY");
            else if (reminder.RecurringInterval == "Weekly")
                sb.AppendLine("RRULE:FREQ=WEEKLY");
            else if (reminder.RecurringInterval == "Monthly")
                sb.AppendLine("RRULE:FREQ=MONTHLY");

            sb.AppendLine("END:VEVENT");
        }

        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    private static string EscapeIcs(string value) =>
        value.Replace("\\", "\\\\")
             .Replace(";", "\\;")
             .Replace(",", "\\,")
             .Replace("\n", "\\n")
             .Replace("\r", "");
}
