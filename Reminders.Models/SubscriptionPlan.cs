namespace Reminders.Models;

public class SubscriptionPlan
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string BillingInterval { get; set; } = "Monthly"; // Monthly, Yearly
    public string AllowedChannelsJson { get; set; } = "[]"; // JSON array of NotificationChannel names
    public int FreeNotificationQuota { get; set; } = 10;
}
