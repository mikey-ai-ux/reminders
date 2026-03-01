using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Business.Interfaces;

public interface ISubscriptionService
{
    bool IsChannelAllowed(AppUser user, NotificationChannel channel);
    Task<string?> CreateCheckoutSessionAsync(AppUser user, int planId, string successUrl, string cancelUrl);
    Task<string?> CreateCustomerPortalSessionAsync(AppUser user, string returnUrl);
    Task HandleStripeWebhookAsync(string payload, string signature);
    Task<int> GetCurrentMonthUsageAsync(string userId);
}
