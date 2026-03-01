namespace Reminders.Models.Settings;

public class TwilioSettings
{
    public string AccountSid { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public string FromPhone { get; set; } = string.Empty;
}
