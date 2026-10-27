namespace WebhookDelivery.Infrastructure.Data;

public class Event
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ApplicationId { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; } // raw JSON, stored as jsonb
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FanOutCompletedAt { get; set; } // null = still needs fan-out
}
