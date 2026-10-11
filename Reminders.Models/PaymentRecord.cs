namespace Reminders.Models;

public record PaymentRecord(DateTime Date, string Description, decimal Amount, string Currency, string Status, string? InvoiceUrl);
