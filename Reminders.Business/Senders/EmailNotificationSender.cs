using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Reminders.Business.Interfaces;
using Reminders.Models;
using Reminders.Models.Settings;

namespace Reminders.Business.Senders;

public class EmailNotificationSender : IEmailNotificationSender
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<EmailNotificationSender> _logger;
    private readonly IReminderActionLinks? _links;

    public EmailNotificationSender(IOptions<SmtpSettings> settings, ILogger<EmailNotificationSender> logger, IReminderActionLinks? links = null)
    {
        _settings = settings.Value;
        _logger = logger;
        _links = links;
    }

    public async Task SendAsync(AppUser user, Reminder reminder)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            _logger.LogWarning("Cannot send email: user {UserId} has no email address", user.Id);
            return;
        }

        // Fail fast instead of opening a connection with placeholder credentials.
        if (string.IsNullOrWhiteSpace(_settings.Host) || string.IsNullOrWhiteSpace(_settings.Password)
            || _settings.Password.Contains("YOUR_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Email delivery is not configured (Smtp settings).");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
        message.To.Add(new MailboxAddress(user.DisplayName ?? user.Email, user.Email));
        message.Subject = $"Reminder: {reminder.Name}";

        var occurTime = reminder.OccursAt;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(reminder.TimeZoneId);
            occurTime = TimeZoneInfo.ConvertTimeFromUtc(reminder.OccursAt, tz);
        }
        catch { /* fallback to UTC */ }

        var formattedTime = occurTime.ToString("dddd, MMMM d yyyy 'at' h:mm tt");
        var description = string.IsNullOrWhiteSpace(reminder.Description)
            ? ""
            : $"<p style=\"color:#64748b;font-size:15px;\">{System.Web.HttpUtility.HtmlEncode(reminder.Description)}</p>";

        // The link opens a page with the buttons rather than acting directly, so mail scanners
        // that pre-open links can't snooze or complete a reminder.
        var actionUrl = _links?.Absolute(_links.ActionPagePath(reminder));
        var actionsHtml = actionUrl is null
            ? ""
            : $"<p style=\"margin:0 0 8px;\"><a href=\"{System.Web.HttpUtility.HtmlAttributeEncode(actionUrl)}\" style=\"display:inline-block;background:#4361ee;color:#ffffff;text-decoration:none;font-weight:600;font-size:14px;padding:10px 18px;border-radius:8px;\">Snooze or mark as done</a></p>";

        var html = $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
            <body style="margin:0;padding:0;background:#f8f9fa;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
              <div style="max-width:600px;margin:40px auto;background:#ffffff;border-radius:16px;overflow:hidden;box-shadow:0 4px 24px rgba(0,0,0,0.08);">
                <div style="background:linear-gradient(135deg,#4361ee,#7b5ea7);padding:32px 40px;text-align:center;">
                  <div style="font-size:48px;margin-bottom:8px;">⏰</div>
                  <h1 style="color:#ffffff;margin:0;font-size:24px;font-weight:700;">Reminder</h1>
                </div>
                <div style="padding:40px;">
                  <h2 style="color:#1a1a2e;font-size:20px;font-weight:700;margin:0 0 12px;">{System.Web.HttpUtility.HtmlEncode(reminder.Name)}</h2>
                  {description}
                  <div style="background:#f8f9fa;border-radius:10px;padding:16px;margin:24px 0;">
                    <p style="margin:0;font-size:14px;color:#64748b;">Scheduled for</p>
                    <p style="margin:4px 0 0;font-size:18px;font-weight:700;color:#4361ee;">{formattedTime}</p>
                    <p style="margin:4px 0 0;font-size:13px;color:#64748b;">{reminder.TimeZoneId}</p>
                  </div>
                  {(reminder.RecurringInterval != "None" ? $"<p style=\"font-size:13px;color:#64748b;\">Repeats: {System.Web.HttpUtility.HtmlEncode(reminder.RecurringInterval.Replace("Custom:", ""))}</p>" : "")}
                  {actionsHtml}
                  <p style="font-size:12px;color:#94a3b8;margin-top:32px;">You received this because you set up a reminder in Reminders App.</p>
                </div>
              </div>
            </body>
            </html>
            """;

        var plainText = $"Reminder: {reminder.Name}\n" +
                        (string.IsNullOrWhiteSpace(reminder.Description) ? "" : $"{reminder.Description}\n") +
                        $"Scheduled for: {formattedTime} ({reminder.TimeZoneId})" +
                        (actionUrl is null ? "" : $"\n\nSnooze or mark as done: {actionUrl}");

        var bodyBuilder = new BodyBuilder
        {
            HtmlBody = html,
            TextBody = plainText
        };
        message.Body = bodyBuilder.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_settings.Host, _settings.Port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_settings.Username, _settings.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
            _logger.LogInformation("Email sent to {Email} for reminder '{Name}'", user.Email, reminder.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email} for reminder '{Name}'", user.Email, reminder.Name);
            throw;
        }
    }
}
