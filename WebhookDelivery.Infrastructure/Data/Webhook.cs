namespace WebhookDelivery.Infrastructure.Data;

public class Webhook
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ApplicationId { get; set; }
    public required string Url { get; set; }
    public string? Secret { get; set; } // for HMAC signing later
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
