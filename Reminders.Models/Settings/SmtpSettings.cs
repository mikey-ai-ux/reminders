namespace Reminders.Models.Settings;

public class SmtpSettings
{
    public string Host { get; set; } = "smtp.sendgrid.net";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "noreply@yourapp.com";
    public string FromName { get; set; } = "Reminders App";
}
