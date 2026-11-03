using Microsoft.Extensions.Options;
using WebhookDelivery.Infrastructure.Services;

namespace WebhookDelivery.Worker;

public class WorkerOptions
{
    public bool Once { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
}

public sealed class DeliveryWorker(
    IServiceScopeFactory scopes,
    IOptions<WorkerOptions> workerOptions,
    IOptions<DeliveryOptions> deliveryOptions,
    IHostApplicationLifetime lifetime,
    ILogger<DeliveryWorker> log
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var w = workerOptions.Value;
        log.LogInformation("Worker started (once={Once})", w.Once);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var didWork = await RunCycleAsync(ct);
                if (didWork)
                    continue;
                if (w.Once)
                    break;
                await Task.Delay(w.PollIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Worker cycle failed");
                if (w.Once)
                {
                    Environment.ExitCode = 1;
                    break;
                }
                await Task.Delay(2000, ct);
            }
        }
        if (w.Once)
            lifetime.StopApplication();
    }

    private async Task<bool> RunCycleAsync(CancellationToken ct)
    {
        // 1. Fan-out
        using (var scope = scopes.CreateScope())
        {
            var fan = scope.ServiceProvider.GetRequiredService<FanOutService>();
            var res = await fan.RunOnceAsync(ct);
            if (res.ClaimedEvent)
            {
                log.LogInformation("Fan-out created {Count} deliveries", res.DeliveriesCreated);
                return true;
            }
        }

        List<ClaimedDelivery> batch;
        using (var scope = scopes.CreateScope())
            batch = await scope
                .ServiceProvider.GetRequiredService<DeliveryStore>()
                .ClaimBatchAsync(ct);
        if (batch.Count == 0)
            return false;

        await Parallel.ForEachAsync(
            batch,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = deliveryOptions.Value.Concurrency,
                CancellationToken = ct,
            },
            async (d, token) =>
            {
                using var scope = scopes.CreateScope();
                var sender = scope.ServiceProvider.GetRequiredService<DeliverySender>();
                var store = scope.ServiceProvider.GetRequiredService<DeliveryStore>();

                var outcome = await sender.SendAsync(d, token);
                var written = await store.RecordAsync(d, outcome, token);
                if (!written)
                    log.LogWarning("Claim lost for delivery {Id}; result discarded", d.Id);
            }
        );

        return true;
    }
}
