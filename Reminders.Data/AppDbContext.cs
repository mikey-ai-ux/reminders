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
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();
    public DbSet<PushSubscriptionRecord> PushSubscriptions => Set<PushSubscriptionRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.TimeZoneId).HasMaxLength(100).HasDefaultValue("UTC");
            e.Property(u => u.StripeCustomerId).HasMaxLength(200);
            e.Property(u => u.StripeSubscriptionId).HasMaxLength(200);
        });

        builder.Entity<Reminder>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.TimeZoneId).HasMaxLength(100);
            e.Property(r => r.RecurringInterval).HasMaxLength(20).HasDefaultValue("None");
            e.HasOne(r => r.User)
             .WithMany(u => u.Reminders)
             .HasForeignKey(r => r.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReminderNotification>(e =>
        {
            e.HasKey(rn => rn.Id);
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

        builder.Entity<SubscriptionPlan>(e =>
        {
            e.HasKey(sp => sp.Id);
            e.Property(sp => sp.Name).HasMaxLength(100).IsRequired();
            e.Property(sp => sp.Price).HasPrecision(10, 2);
        });

        builder.Entity<PushSubscriptionRecord>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.UserId).IsRequired();
            e.Property(p => p.Endpoint).HasMaxLength(2048).IsRequired();
            e.Property(p => p.P256dh).HasMaxLength(512).IsRequired();
            e.Property(p => p.Auth).HasMaxLength(256).IsRequired();
            e.HasOne(p => p.User)
             .WithMany(u => u.PushSubscriptions)
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // Seed subscription plans
        builder.Entity<SubscriptionPlan>().HasData(
            new SubscriptionPlan { Id = 1, Name = "Free", Price = 0, BillingInterval = "Monthly", AllowedChannelsJson = "[\"Email\",\"Push\"]", FreeNotificationQuota = 10 },
            new SubscriptionPlan { Id = 2, Name = "Pro", Price = 9.99m, BillingInterval = "Monthly", AllowedChannelsJson = "[\"Email\",\"Push\",\"SMS\",\"Voice\"]", FreeNotificationQuota = 1000 }
        );
    }
}
