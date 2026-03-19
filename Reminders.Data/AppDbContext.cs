using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Reminders.Models;

namespace Reminders.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<ReminderNotification> ReminderNotifications => Set<ReminderNotification>();
    public DbSet<ReminderChannel> ReminderChannels => Set<ReminderChannel>();
    public DbSet<ReminderTarget> ReminderTargets => Set<ReminderTarget>();
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();
    public DbSet<UserContactEndpoint> UserContactEndpoints => Set<UserContactEndpoint>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.TimeZoneId).HasMaxLength(100).HasDefaultValue("UTC");
            e.Property(u => u.StripeCustomerId).HasMaxLength(200);
            e.Property(u => u.StripeSubscriptionId).HasMaxLength(200);
            e.HasOne(u => u.SubscriptionPlan)
             .WithMany()
             .HasForeignKey(u => u.SubscriptionPlanId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Reminder>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.TimeZoneId).HasMaxLength(100);
            e.Property(r => r.Icon).HasMaxLength(20).HasDefaultValue("🔔");
            e.Property(r => r.CustomIconUrl).HasMaxLength(1024);
            e.Property(r => r.RecurringInterval).HasMaxLength(100).HasDefaultValue("None");
            e.HasOne(r => r.User)
             .WithMany(u => u.Reminders)
             .HasForeignKey(r => r.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReminderNotification>(e =>
        {
            e.HasKey(rn => rn.Id);
            e.Property(rn => rn.ReminderName).HasMaxLength(200).IsRequired();
            e.Property(rn => rn.MessageSnapshot).HasMaxLength(2000).IsRequired();
            e.Property(rn => rn.TimeZoneId).HasMaxLength(100).HasDefaultValue("UTC");
            e.Property(rn => rn.DeviceType).HasMaxLength(80);
            e.HasOne(rn => rn.Reminder)
             .WithMany(r => r.Notifications)
             .HasForeignKey(rn => rn.ReminderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReminderChannel>(e =>
        {
            e.HasKey(rc => rc.Id);
            e.HasOne(rc => rc.Reminder)
             .WithMany(r => r.Channels)
             .HasForeignKey(rc => rc.ReminderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReminderTarget>(e =>
        {
            e.HasKey(rt => rt.Id);
            e.Property(rt => rt.TargetType).HasMaxLength(40).IsRequired();
            e.Property(rt => rt.TargetValue).HasMaxLength(2048).IsRequired();
            e.Property(rt => rt.Label).HasMaxLength(120);
            e.HasOne(rt => rt.Reminder)
             .WithMany(r => r.Targets)
             .HasForeignKey(rt => rt.ReminderId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SubscriptionPlan>(e =>
        {
            e.HasKey(sp => sp.Id);
            e.Property(sp => sp.Name).HasMaxLength(100).IsRequired();
            e.Property(sp => sp.Price).HasPrecision(10, 2);
            e.Property(sp => sp.StripePriceId).HasMaxLength(200);
        });

        builder.Entity<PushSubscriptionRecord>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.UserId).IsRequired();
            e.Property(p => p.Endpoint).HasMaxLength(2048).IsRequired();
            e.Property(p => p.P256dh).HasMaxLength(512).IsRequired();
            e.Property(p => p.Auth).HasMaxLength(256).IsRequired();
            e.Property(p => p.DeviceType).HasMaxLength(80).HasDefaultValue("Unknown");
            e.HasOne(p => p.User)
             .WithMany(u => u.PushSubscriptions)
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserContactEndpoint>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.UserId).IsRequired();
            e.Property(x => x.Value).HasMaxLength(320).IsRequired();
            e.Property(x => x.Label).HasMaxLength(100);
            e.Property(x => x.VerificationToken).HasMaxLength(120);
            e.Property(x => x.VerificationCode).HasMaxLength(20);
            e.HasOne(x => x.User)
             .WithMany(u => u.ContactEndpoints)
             .HasForeignKey(x => x.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Seed subscription plans
        builder.Entity<SubscriptionPlan>().HasData(
            new SubscriptionPlan
            {
                Id = 1,
                Name = "Free",
                Price = 0,
                BillingInterval = "Monthly",
                AllowedChannelsJson = "[\"Email\",\"Push\"]",
                SmsMonthlyLimit = 0,
                VoiceMonthlyLimit = 0,
                FreeNotificationQuota = 10
            },
            new SubscriptionPlan
            {
                Id = 2,
                Name = "Starter",
                Price = 9.99m,
                BillingInterval = "Monthly",
                AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]",
                SmsMonthlyLimit = 100,
                VoiceMonthlyLimit = 10,
                FreeNotificationQuota = 1000
            },
            new SubscriptionPlan
            {
                Id = 3,
                Name = "Growth",
                Price = 29.99m,
                BillingInterval = "Monthly",
                AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]",
                SmsMonthlyLimit = 2000,
                VoiceMonthlyLimit = 200,
                FreeNotificationQuota = 1000
            },
            new SubscriptionPlan
            {
                Id = 4,
                Name = "Scale",
                Price = 99.99m,
                BillingInterval = "Monthly",
                AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]",
                SmsMonthlyLimit = 10000,
                VoiceMonthlyLimit = 1000,
                FreeNotificationQuota = 1000
            }
        );
    }
}
