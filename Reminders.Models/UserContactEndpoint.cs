using Reminders.Models.Enums;

namespace Reminders.Models;

public class UserContactEndpoint
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public NotificationChannel Channel { get; set; } // Email or SMS/Voice
    public string Value { get; set; } = string.Empty; // email or phone
    public string? Label { get; set; }
    public bool IsConfirmed { get; set; } = false;

    public string? VerificationToken { get; set; }
    public string? VerificationCode { get; set; }
    public DateTime? VerificationExpiresAtUtc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AppUser? User { get; set; }
}
