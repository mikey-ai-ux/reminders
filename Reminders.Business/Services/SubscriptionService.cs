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
using System.Text.Json;

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
        if (IsBillingConfigured)
            StripeConfiguration.ApiKey = _stripeSettings.SecretKey;
    }

    public bool IsBillingConfigured => LooksConfigured(_stripeSettings.SecretKey, "sk_");

    private static bool LooksConfigured(string? value, string prefix) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && !value.Contains("YOUR", StringComparison.OrdinalIgnoreCase)
        && !value.Contains("...", StringComparison.Ordinal);

    public bool IsChannelAllowed(AppUser user, NotificationChannel channel)
    {
        var plan = ResolvePlan(user);
        var allowed = ParseAllowedChannels(plan.AllowedChannelsJson);
        return allowed.Contains(channel.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> IsChannelWithinQuotaAsync(AppUser user, NotificationChannel channel)
    {
        var plan = await GetEffectivePlanAsync(user);
        if (plan == null) return channel is NotificationChannel.Email or NotificationChannel.Push;

        if (channel == NotificationChannel.SMS)
        {
            if (plan.SmsMonthlyLimit <= 0) return false;
            var used = await GetChannelUsageThisMonthAsync(user.Id, NotificationChannel.SMS);
            return used < plan.SmsMonthlyLimit;
        }

        if (channel == NotificationChannel.Voice)
        {
            if (plan.VoiceMonthlyLimit <= 0) return false;
            var used = await GetChannelUsageThisMonthAsync(user.Id, NotificationChannel.Voice);
            return used < plan.VoiceMonthlyLimit;
        }

        return true;
    }

    public async Task<SubscriptionQuotaInfo> GetQuotaInfoAsync(AppUser user)
    {
        var plan = await GetEffectivePlanAsync(user) ?? ResolvePlan(user);

        var smsUsed = await GetChannelUsageThisMonthAsync(user.Id, NotificationChannel.SMS);
        var voiceUsed = await GetChannelUsageThisMonthAsync(user.Id, NotificationChannel.Voice);

        return new SubscriptionQuotaInfo
        {
            PlanName = plan.Name,
            SmsUsedThisMonth = smsUsed,
            SmsLimitPerMonth = plan.SmsMonthlyLimit,
            VoiceUsedThisMonth = voiceUsed,
            VoiceLimitPerMonth = plan.VoiceMonthlyLimit
        };
    }

    public async Task<string?> CreateCheckoutSessionAsync(AppUser user, int planId, string successUrl, string cancelUrl)
    {
        if (!IsBillingConfigured)
        {
            _logger.LogWarning("Stripe is not configured; cannot start checkout for plan {PlanId}", planId);
            return null;
        }

        try
        {
            var plan = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId);
            var priceId = plan?.StripePriceId;

            if (string.IsNullOrWhiteSpace(priceId))
            {
                // Backward-compatible fallback
                priceId = _stripeSettings.ProPlanPriceId;
            }

            if (!LooksConfigured(priceId, "price_"))
            {
                _logger.LogWarning("Stripe price id not configured for plan {PlanId}", planId);
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
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                Metadata = new Dictionary<string, string>
                {
                    { "userId", user.Id },
                    { "planId", planId.ToString() }
                }
            };

            // Stripe rejects a session that sets both Customer and CustomerEmail.
            if (!string.IsNullOrWhiteSpace(user.StripeCustomerId))
                options.Customer = user.StripeCustomerId;
            else
                options.CustomerEmail = user.Email;

            var service = new SessionService();
            var session = await service.CreateAsync(options);
            _logger.LogInformation("Created Stripe checkout session {SessionId} for user {UserId} plan {PlanId}", session.Id, user.Id, planId);
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
        if (!IsBillingConfigured) return null;

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
                var planIdRaw = session.Metadata?.GetValueOrDefault("planId");
                if (userId == null) break;
                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) break;

                user.StripeCustomerId = session.CustomerId;

                if (int.TryParse(planIdRaw, out var parsedPlanId))
                {
                    var plan = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == parsedPlanId);
                    if (plan != null)
                    {
                        user.SubscriptionPlanId = plan.Id;
                        user.SubscriptionTier = TierForPlan(plan);
                    }
                }

                await _userManager.UpdateAsync(user);
                _logger.LogInformation("Linked Stripe customer and plan for user {UserId}", userId);
                break;
            }

            case "customer.subscription.created":
            case "customer.subscription.updated":
            {
                if (stripeEvent.Data.Object is not Subscription sub) break;
                var user = await FindUserByStripeCustomerId(sub.CustomerId);
                if (user == null) break;
                user.StripeSubscriptionId = sub.Id;
                user.SubscriptionExpiresAt = sub.CurrentPeriodEnd;

                if (sub.Status is "active" or "trialing")
                {
                    // The plan can change from the billing portal, so trust the subscribed price over what we stored at checkout.
                    var priceId = sub.Items?.Data?.FirstOrDefault()?.Price?.Id;
                    var plan = string.IsNullOrWhiteSpace(priceId)
                        ? null
                        : await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.StripePriceId == priceId);
                    plan ??= user.SubscriptionPlanId.HasValue
                        ? await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == user.SubscriptionPlanId.Value)
                        : null;

                    if (plan != null && plan.Price > 0)
                    {
                        user.SubscriptionPlanId = plan.Id;
                        user.SubscriptionTier = TierForPlan(plan);
                    }
                    else
                    {
                        user.SubscriptionTier = SubscriptionTier.Pro;
                    }
                }
                else
                {
                    user.SubscriptionTier = SubscriptionTier.Free;
                    user.SubscriptionPlanId = 1;
                }

                await _userManager.UpdateAsync(user);
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
                user.SubscriptionPlanId = 1;
                await _userManager.UpdateAsync(user);
                break;
            }

            case "invoice.payment_failed":
            {
                if (stripeEvent.Data.Object is not Invoice invoice) break;
                _logger.LogWarning("Payment failed for customer {CustomerId}, invoice {InvoiceId}", invoice.CustomerId, invoice.Id);
                break;
            }
        }
    }

    public async Task<List<PaymentRecord>> GetPaymentHistoryAsync(AppUser user)
    {
        if (!IsBillingConfigured || string.IsNullOrWhiteSpace(user.StripeCustomerId))
            return [];

        try
        {
            var invoices = await new InvoiceService().ListAsync(new InvoiceListOptions
            {
                Customer = user.StripeCustomerId,
                Limit = 50
            });

            return invoices.Data
                .Select(i => new PaymentRecord(
                    i.Created,
                    i.Lines?.Data?.FirstOrDefault()?.Description ?? i.Number ?? "Subscription",
                    (i.Status == "paid" ? i.AmountPaid : i.AmountDue) / 100m,
                    (i.Currency ?? "usd").ToUpperInvariant(),
                    i.Status switch
                    {
                        "paid" => "Paid",
                        "open" or "draft" => "Pending",
                        _ => "Failed"
                    },
                    i.HostedInvoiceUrl))
                .OrderByDescending(p => p.Date)
                .ToList();
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe error loading invoices for user {UserId}", user.Id);
            return [];
        }
    }

    public async Task<SubscriptionPlan?> GetCurrentPlanAsync(AppUser user) => await GetEffectivePlanAsync(user);

    public async Task<bool> CancelSubscriptionAsync(AppUser user)
    {
        if (string.IsNullOrWhiteSpace(user.StripeSubscriptionId)) return true;
        if (!IsBillingConfigured)
        {
            _logger.LogWarning("Cannot cancel subscription {SubscriptionId} for user {UserId}: Stripe is not configured", user.StripeSubscriptionId, user.Id);
            return false;
        }

        try
        {
            await new Stripe.SubscriptionService().CancelAsync(user.StripeSubscriptionId);
            return true;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            return true; // already gone on Stripe's side
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe error cancelling subscription for user {UserId}", user.Id);
            return false;
        }
    }

    private static SubscriptionTier TierForPlan(SubscriptionPlan plan) => plan.Name switch
    {
        "Free" => SubscriptionTier.Free,
        "Starter" => SubscriptionTier.Pro,
        "Growth" => SubscriptionTier.Business,
        "Scale" => SubscriptionTier.Enterprise,
        _ => plan.Price > 0 ? SubscriptionTier.Pro : SubscriptionTier.Free
    };

    public async Task<int> GetCurrentMonthUsageAsync(string userId)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.ReminderNotifications
            .AsNoTracking()
            .CountAsync(n => n.Reminder!.UserId == userId
                && n.Status == NotificationStatus.Sent
                && n.SentAt >= monthStart);
    }

    private async Task<int> GetChannelUsageThisMonthAsync(string userId, NotificationChannel channel)
    {
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return await _db.ReminderNotifications
            .AsNoTracking()
            .CountAsync(n => n.Reminder != null
                && n.Reminder.UserId == userId
                && n.Channel == channel
                && n.Status == NotificationStatus.Sent
                && n.SentAt >= monthStart);
    }

    private async Task<SubscriptionPlan?> GetEffectivePlanAsync(AppUser user)
    {
        if (user.SubscriptionPlanId.HasValue)
        {
            var byId = await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == user.SubscriptionPlanId.Value);
            if (byId != null) return byId;
        }

        // fallback by tier
        var fallbackName = user.SubscriptionTier switch
        {
            SubscriptionTier.Free => "Free",
            SubscriptionTier.Pro => "Starter",
            SubscriptionTier.Business => "Growth",
            SubscriptionTier.Enterprise => "Scale",
            _ => "Free"
        };

        return await _db.SubscriptionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Name == fallbackName)
               ?? await _db.SubscriptionPlans.AsNoTracking().OrderBy(p => p.Price).FirstOrDefaultAsync();
    }

    private SubscriptionPlan ResolvePlan(AppUser user)
    {
        // Fast sync fallback for UI checks
        return user.SubscriptionTier switch
        {
            SubscriptionTier.Free => new SubscriptionPlan { Name = "Free", AllowedChannelsJson = "[\"Email\",\"Push\"]", SmsMonthlyLimit = 0, VoiceMonthlyLimit = 0 },
            SubscriptionTier.Pro => new SubscriptionPlan { Name = "Starter", AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]", SmsMonthlyLimit = 100, VoiceMonthlyLimit = 10 },
            SubscriptionTier.Business => new SubscriptionPlan { Name = "Growth", AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]", SmsMonthlyLimit = 2000, VoiceMonthlyLimit = 200 },
            SubscriptionTier.Enterprise => new SubscriptionPlan { Name = "Scale", AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]", SmsMonthlyLimit = 10000, VoiceMonthlyLimit = 1000 },
            _ => new SubscriptionPlan { Name = "Free", AllowedChannelsJson = "[\"Email\",\"Push\"]" }
        };
    }

    private static string[] ParseAllowedChannels(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<AppUser?> FindUserByStripeCustomerId(string customerId)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.StripeCustomerId == customerId);
    }
}
