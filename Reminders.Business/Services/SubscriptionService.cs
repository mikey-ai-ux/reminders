using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Enums;
using Reminders.Models.Settings;
using Stripe;
using Stripe.Checkout;

namespace Reminders.Business.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly StripeSettings _stripeSettings;
    private readonly IConfiguration _config;
    private readonly AppDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<SubscriptionService> _logger;

    public SubscriptionService(
        IOptions<StripeSettings> stripeSettings,
        IConfiguration config,
        AppDbContext db,
        UserManager<AppUser> userManager,
        ILogger<SubscriptionService> logger)
    {
        _stripeSettings = stripeSettings.Value;
        _config = config;
        _db = db;
        _userManager = userManager;
        _logger = logger;
        StripeConfiguration.ApiKey = _stripeSettings.SecretKey;
    }

    public bool IsChannelAllowed(AppUser user, NotificationChannel channel)
    {
        var channelName = channel.ToString();
        if (user.SubscriptionTier == SubscriptionTier.Free)
        {
            var freeChannels = _config.GetSection("Notifications:FreeChannels")
                .GetChildren().Select(c => c.Value ?? "").ToArray();
            if (freeChannels.Length == 0) freeChannels = ["Email", "Push"];
            return freeChannels.Contains(channelName, StringComparer.OrdinalIgnoreCase);
        }
        var paidChannels = _config.GetSection("Notifications:PaidChannels")
            .GetChildren().Select(c => c.Value ?? "").ToArray();
        if (paidChannels.Length == 0) paidChannels = ["Email", "Push", "SMS", "Voice"];
        return paidChannels.Contains(channelName, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string?> CreateCheckoutSessionAsync(AppUser user, int planId, string successUrl, string cancelUrl)
    {
        try
        {
            var priceId = _stripeSettings.ProPlanPriceId;
            if (string.IsNullOrWhiteSpace(priceId))
            {
                _logger.LogWarning("Stripe ProPlanPriceId not configured");
                return null;
            }

            var options = new SessionCreateOptions
            {
                Mode = "subscription",
                LineItems =
                [
                    new SessionLineItemOptions
                    {
                        Price = priceId,
                        Quantity = 1
                    }
                ],
                CustomerEmail = user.Email,
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string> { { "userId", user.Id } }
            };

            if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
                options.Customer = user.StripeCustomerId;

            var service = new SessionService();
            var session = await service.CreateAsync(options);
            _logger.LogInformation("Created Stripe checkout session {SessionId} for user {UserId}", session.Id, user.Id);
            return session.Url;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe error creating checkout session for user {UserId}", user.Id);
            return null;
        }
    }

    public async Task<string?> CreateCustomerPortalSessionAsync(AppUser user, string returnUrl)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(user.StripeCustomerId))
            {
                _logger.LogWarning("User {UserId} has no StripeCustomerId for portal session", user.Id);
                return null;
            }

            var options = new Stripe.BillingPortal.SessionCreateOptions
            {
                Customer = user.StripeCustomerId,
                ReturnUrl = returnUrl
            };
            var service = new Stripe.BillingPortal.SessionService();
            var session = await service.CreateAsync(options);
            _logger.LogInformation("Created Stripe billing portal session for user {UserId}", user.Id);
            return session.Url;
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe error creating portal session for user {UserId}", user.Id);
            return null;
        }
    }

    public async Task HandleStripeWebhookAsync(string payload, string signature)
    {
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, _stripeSettings.WebhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Invalid Stripe webhook signature");
            throw;
        }

        _logger.LogInformation("Handling Stripe webhook event: {EventType}", stripeEvent.Type);

        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
            {
                if (stripeEvent.Data.Object is not Session session) break;
                var userId = session.Metadata?.GetValueOrDefault("userId");
                if (userId == null) break;
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) break;
                user.StripeCustomerId = session.CustomerId;
                await _userManager.UpdateAsync(user);
                _logger.LogInformation("Linked StripeCustomerId {CustomerId} to user {UserId}", session.CustomerId, userId);
                break;
            }

            case "customer.subscription.created":
            case "customer.subscription.updated":
            {
                if (stripeEvent.Data.Object is not Subscription sub) break;
                var user = await FindUserByStripeCustomerId(sub.CustomerId);
                if (user == null) break;
                user.StripeSubscriptionId = sub.Id;
                user.SubscriptionTier = sub.Status is "active" or "trialing" ? SubscriptionTier.Pro : SubscriptionTier.Free;
                user.SubscriptionExpiresAt = sub.CurrentPeriodEnd;
                await _userManager.UpdateAsync(user);
                _logger.LogInformation("Updated subscription for user {UserId}: tier={Tier}, status={Status}", user.Id, user.SubscriptionTier, sub.Status);
                break;
            }

            case "customer.subscription.deleted":
            {
                if (stripeEvent.Data.Object is not Subscription sub) break;
                var user = await FindUserByStripeCustomerId(sub.CustomerId);
                if (user == null) break;
                user.SubscriptionTier = SubscriptionTier.Free;
                user.SubscriptionExpiresAt = null;
                user.StripeSubscriptionId = null;
                await _userManager.UpdateAsync(user);
                _logger.LogInformation("Downgraded user {UserId} to Free tier (subscription deleted)", user.Id);
                break;
            }

            case "invoice.payment_failed":
            {
                if (stripeEvent.Data.Object is not Invoice invoice) break;
                _logger.LogWarning("Payment failed for customer {CustomerId}, invoice {InvoiceId}", invoice.CustomerId, invoice.Id);
                // Optionally notify user via email here
                break;
            }

            default:
                _logger.LogDebug("Unhandled Stripe event type: {EventType}", stripeEvent.Type);
                break;
        }
    }

    public async Task<int> GetCurrentMonthUsageAsync(string userId)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.ReminderNotifications
            .AsNoTracking()
            .CountAsync(n => n.Reminder!.UserId == userId
                && n.Status == NotificationStatus.Sent
                && n.SentAt >= monthStart);
    }

    private async Task<AppUser?> FindUserByStripeCustomerId(string customerId)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.StripeCustomerId == customerId);
    }
}
