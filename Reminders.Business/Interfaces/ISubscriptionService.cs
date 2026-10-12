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

    /// <summary>False while Stripe keys are still placeholders; checkout and the billing portal are unavailable then.</summary>
    bool IsBillingConfigured { get; }
    Task<SubscriptionPlan?> GetCurrentPlanAsync(AppUser user);
    Task<List<PaymentRecord>> GetPaymentHistoryAsync(AppUser user);

    /// <summary>
    /// Cancels the user's paid subscription with the payment provider. Returns true when there is
    /// nothing left to bill (including when the user never had one), false when it could not be cancelled.
    /// </summary>
    Task<bool> CancelSubscriptionAsync(AppUser user);
}
