namespace Reminders.Models;

public class SubscriptionQuotaInfo
{
    public string PlanName { get; set; } = "Free";
    public int SmsUsedThisMonth { get; set; }
    public int SmsLimitPerMonth { get; set; }
    public int VoiceUsedThisMonth { get; set; }
    public int VoiceLimitPerMonth { get; set; }
}
