using Reminders.Models.Enums;

namespace Reminders.Models;

/// <summary>Filter, sort and page selection for the notification history.</summary>
public class NotificationHistoryQuery
{
    public const int MaxPageSize = 100;

    public string? Search { get; set; }
    public NotificationChannel? Channel { get; set; }
    public NotificationStatus? Status { get; set; }
    /// <summary>sent_desc (default), sent_asc, channel_asc or status_asc.</summary>
    public string Sort { get; set; } = "sent_desc";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public record NotificationHistoryPage(List<ReminderNotification> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}