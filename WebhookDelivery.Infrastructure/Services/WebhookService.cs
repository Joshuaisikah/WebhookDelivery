using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Services;

public interface IWebhookService
{
    Task<Webhook?> CreateAsync(Guid applicationId, string url, CancellationToken ct);
}

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
}
