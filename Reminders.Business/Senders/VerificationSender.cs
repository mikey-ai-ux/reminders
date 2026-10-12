using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Reminders.Business.Interfaces;
using Reminders.Models.Settings;
using Twilio.Clients;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Reminders.Business.Senders;

/// <summary>Sends account messages (email confirmation links, phone codes). Never throws: returns false when it could not send.</summary>
public class VerificationSender : IVerificationSender
{
    private readonly SmtpSettings _smtp;
    private readonly TwilioSettings _twilio;
    private readonly ILogger<VerificationSender> _logger;

    public VerificationSender(IOptions<SmtpSettings> smtp, IOptions<TwilioSettings> twilio, ILogger<VerificationSender> logger)
    {
        _smtp = smtp.Value;
        _twilio = twilio.Value;
        _logger = logger;
    }

    public bool IsEmailConfigured =>
        IsSet(_smtp.Host) && IsSet(_smtp.Username) && IsSet(_smtp.Password) && IsSet(_smtp.FromEmail);

    public bool IsSmsConfigured =>
        IsSet(_twilio.AccountSid) && IsSet(_twilio.AuthToken) && IsSet(_twilio.FromPhone);

    public async Task<bool> SendEmailAsync(string to, string subject, string body)
    {
        if (!IsEmailConfigured)
        {
            _logger.LogWarning("SMTP is not configured; email to {Email} was not sent", to);
            return false;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_smtp.FromName, _smtp.FromEmail));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(_smtp.Host, _smtp.Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}", to);
            return false;
        }
    }

    public async Task<bool> SendSmsAsync(string to, string text)
    {
        if (!IsSmsConfigured)
        {
            _logger.LogWarning("Twilio is not configured; SMS to {Phone} was not sent", to);
            return false;
        }

        try
        {
            var client = new TwilioRestClient(_twilio.AccountSid, _twilio.AuthToken);
            await MessageResource.CreateAsync(
                body: text,
                from: new PhoneNumber(_twilio.FromPhone),
                to: new PhoneNumber(to),
                client: client);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SMS to {Phone}", to);
            return false;
        }
    }

    private static bool IsSet(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);
}
