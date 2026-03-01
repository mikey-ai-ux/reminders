using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reminders.Business.Interfaces;
using Reminders.Models;
using Reminders.Models.Settings;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Reminders.Business.Senders;

public class SmsNotificationSender : ISmsNotificationSender
{
    private readonly TwilioSettings _settings;
    private readonly ILogger<SmsNotificationSender> _logger;

    public SmsNotificationSender(IOptions<TwilioSettings> settings, ILogger<SmsNotificationSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        TwilioClient.Init(_settings.AccountSid, _settings.AuthToken);
    }

    public async Task SendAsync(AppUser user, Reminder reminder)
    {
        var phone = user.PhoneNumber;
        if (string.IsNullOrWhiteSpace(phone))
        {
            _logger.LogWarning("Cannot send SMS: user {UserId} has no phone number", user.Id);
            return;
        }

        var occurTime = reminder.OccursAt;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(reminder.TimeZoneId);
            occurTime = TimeZoneInfo.ConvertTimeFromUtc(reminder.OccursAt, tz);
        }
        catch { /* fallback to UTC */ }

        var body = $"⏰ Reminder: {reminder.Name}" +
                   (string.IsNullOrWhiteSpace(reminder.Description) ? "" : $" — {reminder.Description}") +
                   $"\nScheduled: {occurTime:MMM d, h:mm tt} ({reminder.TimeZoneId})";

        try
        {
            var msg = await MessageResource.CreateAsync(
                body: body,
                from: new PhoneNumber(_settings.FromPhone),
                to: new PhoneNumber(phone)
            );
            _logger.LogInformation("SMS sent to {Phone} for reminder '{Name}', SID: {Sid}", phone, reminder.Name, msg.Sid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS to {Phone} for reminder '{Name}'", phone, reminder.Name);
            throw;
        }
    }
}
