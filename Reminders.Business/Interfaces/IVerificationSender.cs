namespace Reminders.Business.Interfaces;

public interface IVerificationSender
{
    bool IsEmailConfigured { get; }
    bool IsSmsConfigured { get; }
    Task<bool> SendEmailAsync(string to, string subject, string body);
    Task<bool> SendSmsAsync(string to, string text);
}
