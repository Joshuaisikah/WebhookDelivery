using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Services;

public interface IWebhookService
{
    Task<Webhook?> CreateAsync(Guid applicationId, string url, CancellationToken ct);
    Task<WebhookStatusResult?> SetActiveAsync(
        Guid applicationId,
        Guid webhookId,
        bool isActive,
        CancellationToken ct
    );
}

public sealed record WebhookStatusResult(Webhook Webhook, int CancelledDeliveries);

public class WebhookService(AppDbContext db) : IWebhookService
{
    public async Task<Webhook?> CreateAsync(Guid applicationId, string url, CancellationToken ct)
    {
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, ct))
            return null;

        var webhook = new Webhook
        {
            ApplicationId = applicationId,
            Url = url,
            Secret =
                "whsec_"
                + Convert
                    .ToBase64String(RandomNumberGenerator.GetBytes(32))
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .TrimEnd('='),
        };

        db.Webhooks.Add(webhook);
        await db.SaveChangesAsync(ct);
        return webhook;
    }

    public async Task<WebhookStatusResult?> SetActiveAsync(
        Guid applicationId,
        Guid webhookId,
        bool isActive,
        CancellationToken ct
    )
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var updated = await db
            .Webhooks.Where(w => w.Id == webhookId && w.ApplicationId == applicationId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.IsActive, isActive), ct);
        if (updated == 0)
            return null;

        var cancelled = 0;
        if (!isActive)
        {
            cancelled = await db
                .Deliveries.Where(d =>
                    d.WebhookId == webhookId && d.Status == DeliveryStatus.Pending
                )
                .ExecuteUpdateAsync(
                    s =>
                        s.SetProperty(d => d.Status, DeliveryStatus.Cancelled)
                            .SetProperty(d => d.LockedUntil, (DateTimeOffset?)null)
                            .SetProperty(d => d.ClaimToken, (Guid?)null)
                            .SetProperty(d => d.UpdatedAt, DateTimeOffset.UtcNow),
                    ct
                );
        }

        var webhook = await db.Webhooks.AsNoTracking().SingleAsync(w => w.Id == webhookId, ct);
        await tx.CommitAsync(ct);
        return new WebhookStatusResult(webhook, cancelled);
    }
}
