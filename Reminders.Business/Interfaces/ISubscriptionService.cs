using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Business.Interfaces;

public interface ISubscriptionService
{
    bool IsChannelAllowed(AppUser user, NotificationChannel channel);
    Task<bool> IsChannelWithinQuotaAsync(AppUser user, NotificationChannel channel);
    Task<SubscriptionQuotaInfo> GetQuotaInfoAsync(AppUser user);

    Task<string?> CreateCheckoutSessionAsync(AppUser user, int planId, string successUrl, string cancelUrl);
    Task<string?> CreateCustomerPortalSessionAsync(AppUser user, string returnUrl);
    Task HandleStripeWebhookAsync(string payload, string signature);
    Task<int> GetCurrentMonthUsageAsync(string userId);
}
