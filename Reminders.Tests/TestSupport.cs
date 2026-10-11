using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Enums;

namespace Reminders.Tests;

/// <summary>A throwaway SQLite database that lives as long as the test.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public DbContextOptions<AppDbContext> Options =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

    public AppDbContext NewContext() => new(Options);

    public AppUser AddUser(string email = "user@example.com", bool emailConfirmed = true, SubscriptionTier tier = SubscriptionTier.Free, string? phone = null)
    {
        using var db = NewContext();
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = emailConfirmed,
            PhoneNumber = phone,
            PhoneNumberConfirmed = phone != null,
            SubscriptionTier = tier,
            SecurityStamp = Guid.NewGuid().ToString()
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class RecordingSender : IEmailNotificationSender, ISmsNotificationSender, IVoiceNotificationSender, IPushNotificationSender
{
    public List<string> Emails { get; } = [];
    public List<string> Phones { get; } = [];
    public int PushCalls { get; private set; }
    public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int PushDelivered { get; set; } = 1;

    Task IEmailNotificationSender.SendAsync(AppUser user, Reminder reminder)
    {
        if (FailFor.Contains(user.Email!)) throw new InvalidOperationException("smtp down");
        Emails.Add(user.Email!);
        return Task.CompletedTask;
    }

    Task ISmsNotificationSender.SendAsync(AppUser user, Reminder reminder) => Phone(user);
    Task IVoiceNotificationSender.SendAsync(AppUser user, Reminder reminder) => Phone(user);

    private Task Phone(AppUser user)
    {
        if (FailFor.Contains(user.PhoneNumber!)) throw new InvalidOperationException("twilio down");
        Phones.Add(user.PhoneNumber!);
        return Task.CompletedTask;
    }

    public Task<int> SendAsync(AppUser user, Reminder reminder, IEnumerable<string>? endpointFilter = null)
    {
        PushCalls++;
        return Task.FromResult(PushDelivered);
    }
}

public sealed class FakeSubscriptionService : ISubscriptionService
{
    public int SmsLimit { get; set; } = 100;
    public int SmsUsed { get; set; }

    public bool IsBillingConfigured => false;
    public bool IsChannelAllowed(AppUser user, NotificationChannel channel) =>
        user.SubscriptionTier != SubscriptionTier.Free || channel is NotificationChannel.Email or NotificationChannel.Push;

    public Task<bool> IsChannelWithinQuotaAsync(AppUser user, NotificationChannel channel)
    {
        if (channel != NotificationChannel.SMS) return Task.FromResult(true);
        return Task.FromResult(SmsUsed++ < SmsLimit);
    }

    public Task<SubscriptionQuotaInfo> GetQuotaInfoAsync(AppUser user) => Task.FromResult(new SubscriptionQuotaInfo());
    public Task<string?> CreateCheckoutSessionAsync(AppUser user, int planId, string successUrl, string cancelUrl) => Task.FromResult<string?>(null);
    public Task<string?> CreateCustomerPortalSessionAsync(AppUser user, string returnUrl) => Task.FromResult<string?>(null);
    public Task HandleStripeWebhookAsync(string payload, string signature) => Task.CompletedTask;
    public Task<int> GetCurrentMonthUsageAsync(string userId) => Task.FromResult(0);
    public Task<SubscriptionPlan?> GetCurrentPlanAsync(AppUser user) => Task.FromResult<SubscriptionPlan?>(null);
    public Task<List<PaymentRecord>> GetPaymentHistoryAsync(AppUser user) => Task.FromResult(new List<PaymentRecord>());
}
