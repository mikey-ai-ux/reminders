using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reminders.Business.Interfaces;
using Reminders.Models;
using Reminders.Models.Settings;
using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Reminders.Business.Senders;

public class VoiceNotificationSender : IVoiceNotificationSender
{
    private readonly TwilioSettings _settings;
    private readonly ILogger<VoiceNotificationSender> _logger;

    public VoiceNotificationSender(IOptions<TwilioSettings> settings, ILogger<VoiceNotificationSender> logger)
    {
        _settings = settings.Value;
        _logger = logger;
        TwilioClient.Init(_settings.AccountSid, _settings.AuthToken);
    }

    public async Task SendAsync(AppUser user, Reminder reminder)
    {
        // Fail fast instead of calling Twilio with placeholder credentials.
        if (string.IsNullOrWhiteSpace(_settings.AccountSid) || string.IsNullOrWhiteSpace(_settings.AuthToken)
            || _settings.AccountSid.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SMS/voice delivery is not configured (Twilio settings).");

        var phone = user.PhoneNumber;
        if (string.IsNullOrWhiteSpace(phone))
        {
            _logger.LogWarning("Cannot make voice call: user {UserId} has no phone number", user.Id);
            return;
        }

        var message = $"Hello! This is your Reminders App notification. You have a reminder: {reminder.Name}.";
        if (!string.IsNullOrWhiteSpace(reminder.Description))
            message += $" Details: {reminder.Description}.";
        message += " Goodbye!";

        var encodedMessage = System.Web.HttpUtility.HtmlEncode(message);
        var twiml = $"<Response><Say voice=\"alice\" language=\"en-US\">{encodedMessage}</Say><Pause length=\"1\"/></Response>";

        try
        {
            var call = await CallResource.CreateAsync(
                twiml: new Twilio.Types.Twiml(twiml),
                to: new PhoneNumber(phone),
                from: new PhoneNumber(_settings.FromPhone)
            );
            _logger.LogInformation("Voice call initiated to {Phone} for reminder '{Name}', SID: {Sid}", phone, reminder.Name, call.Sid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate voice call to {Phone} for reminder '{Name}'", phone, reminder.Name);
            throw;
        }
    }
}
