namespace WebhookDelivery.Infrastructure.Data;

public class Delivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ApplicationId { get; set; }
    public Guid WebhookId { get; set; }
    public Guid EventId { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LockedUntil { get; set; } // lease expiry
    public Guid? ClaimToken { get; set; } // fencing token
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Webhook Webhook { get; set; } = null!;

    public Event Event { get; set; } = null!;
}
