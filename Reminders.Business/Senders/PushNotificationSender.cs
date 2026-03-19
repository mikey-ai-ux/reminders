using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reminders.Business.Interfaces;
using Reminders.Data;
using Reminders.Models;
using Reminders.Models.Settings;
using System.Text.Json;
using WebPush;

namespace Reminders.Business.Senders;

public class PushNotificationSender : IPushNotificationSender
{
    private readonly VapidSettings _vapid;
    private readonly AppDbContext _db;
    private readonly ILogger<PushNotificationSender> _logger;

    public PushNotificationSender(IOptions<VapidSettings> vapid, AppDbContext db, ILogger<PushNotificationSender> logger)
    {
        _vapid = vapid.Value;
        _db = db;
        _logger = logger;
    }

    public async Task SendAsync(AppUser user, Reminder reminder, IEnumerable<string>? endpointFilter = null)
    {
        var query = _db.PushSubscriptions
            .AsNoTracking()
            .Where(s => s.UserId == user.Id);

        if (endpointFilter != null)
        {
            var endpoints = endpointFilter.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().ToList();
            if (endpoints.Count > 0)
                query = query.Where(s => endpoints.Contains(s.Endpoint));
        }

        var subscriptions = await query.ToListAsync();

        if (!subscriptions.Any())
        {
            _logger.LogInformation("No push subscriptions for user {UserId}", user.Id);
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = $"⏰ {reminder.Name}",
            body = string.IsNullOrWhiteSpace(reminder.Description)
                ? $"Reminder: {reminder.Name}"
                : reminder.Description,
            icon = "/icon-192.png",
            badge = "/badge-72.png",
            url = "/reminders"
        });

        var client = new WebPushClient();
        client.SetVapidDetails(_vapid.Subject, _vapid.PublicKey, _vapid.PrivateKey);

        var staleIds = new List<int>();

        foreach (var sub in subscriptions)
        {
            try
            {
                var pushSub = new PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await client.SendNotificationAsync(pushSub, payload);
                _logger.LogInformation("Push notification sent to user {UserId} endpoint {Endpoint}", user.Id, sub.Endpoint);
            }
            catch (WebPushException ex) when ((int)ex.StatusCode == 410)
            {
                _logger.LogInformation("Stale push subscription {Id} for user {UserId} — removing", sub.Id, user.Id);
                staleIds.Add(sub.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send push notification to user {UserId} endpoint {Endpoint}", user.Id, sub.Endpoint);
            }
        }

        if (staleIds.Any())
        {
            var toRemove = await _db.PushSubscriptions.Where(s => staleIds.Contains(s.Id)).ToListAsync();
            _db.PushSubscriptions.RemoveRange(toRemove);
            await _db.SaveChangesAsync();
        }
    }
}
