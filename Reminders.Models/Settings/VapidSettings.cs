namespace Reminders.Models.Settings;

public class VapidSettings
{
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string Subject { get; set; } = "mailto:admin@yourapp.com";
}
