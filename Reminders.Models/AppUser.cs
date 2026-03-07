using Microsoft.AspNetCore.Identity;
using Reminders.Models.Enums;

namespace Reminders.Models;

public class AppUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public SubscriptionTier SubscriptionTier { get; set; } = SubscriptionTier.Free;
    public DateTime? SubscriptionExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }

    public int? SubscriptionPlanId { get; set; }
    public SubscriptionPlan? SubscriptionPlan { get; set; }

    public ICollection<Reminder> Reminders { get; set; } = new List<Reminder>();
    public ICollection<PushSubscriptionRecord> PushSubscriptions { get; set; } = new List<PushSubscriptionRecord>();
}
