using Microsoft.EntityFrameworkCore;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Services;

public interface IEventService
{
    Task<Event?> CreateAsync(
        Guid applicationId,
        string type,
        string payloadJson,
        CancellationToken ct
    );
}

public class EventService(AppDbContext db) : IEventService
{
    public async Task<Event?> CreateAsync(
        Guid applicationId,
        string type,
        string payloadJson,
        CancellationToken ct
    )
    {
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, ct))
            return null;

        var ev = new Event
        {
            ApplicationId = applicationId,
            Type = type.Trim(),
            Payload = payloadJson,
        };

        db.Events.Add(ev);
        await db.SaveChangesAsync(ct);
        return ev;
    }
}
