using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Services;

public interface IApplicationService
{
    Task<Application> CreateAsync(string name, CancellationToken ct);
}

public class ApplicationService(AppDbContext db) : IApplicationService
{
    public async Task<Application> CreateAsync(string name, CancellationToken ct)
    {
        var app = new Application { Name = name.Trim() };
        db.Applications.Add(app);
        await db.SaveChangesAsync(ct);
        return app;
    }
}
