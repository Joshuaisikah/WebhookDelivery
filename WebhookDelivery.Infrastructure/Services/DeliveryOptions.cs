// DeliveryOptions.cs
namespace WebhookDelivery.Infrastructure.Services;

public sealed class DeliveryOptions
{
    public int MaxAttempts { get; set; } = 8;
    public int LeaseSeconds { get; set; } = 60;
    public int HttpTimeoutSeconds { get; set; } = 10;
    public int BatchSize { get; set; } = 20;
    public int Concurrency { get; set; } = 10;
    public double BackoffBaseSeconds { get; set; } = 5;
    public double BackoffCapSeconds { get; set; } = 3600;

    public void Validate()
    {
        var worst = Math.Ceiling((double)BatchSize / Concurrency) * HttpTimeoutSeconds;
        if (LeaseSeconds <= worst)
            throw new InvalidOperationException(
                $"LeaseSeconds ({LeaseSeconds}) must exceed worst-case batch time ({worst}s)."
            );
        if (MaxAttempts < 1)
            throw new InvalidOperationException("MaxAttempts must be >= 1.");
    }
}
