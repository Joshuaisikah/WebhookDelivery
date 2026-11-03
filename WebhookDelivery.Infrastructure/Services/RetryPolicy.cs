namespace WebhookDelivery.Infrastructure.Services;

public sealed record DeliveryOutcome(
    string Status,
    int? StatusCode,
    string? Error,
    double? RetryDelaySeconds
);

public static class RetryPolicy
{
    public static DeliveryOutcome Delivered(int code) => new("delivered", code, null, null);

    public static DeliveryOutcome Permanent(int? code, string error) =>
        new("failed", code, error, null);

    public static DeliveryOutcome Retryable(int attempt, int? code, string error, DeliveryOptions o)
    {
        if (attempt >= o.MaxAttempts)
            return new("failed", code, $"max attempts reached: {error}", null);

        var exp = Math.Min(o.BackoffCapSeconds, o.BackoffBaseSeconds * Math.Pow(2, attempt - 1));
        var delay = exp * (0.5 + Random.Shared.NextDouble() * 0.5); // jitter: 50-100% of exp
        return new("pending", code, error, delay);
    }

    /// 2xx delivered; 408/429/5xx retried; every other status (incl. 400, 3xx) is permanent.
    public static DeliveryOutcome FromStatus(int code, int attempt, DeliveryOptions o) =>
        code switch
        {
            >= 200 and < 300 => Delivered(code),
            408 or 429 or >= 500 => Retryable(attempt, code, $"HTTP {code}", o),
            _ => Permanent(code, $"HTTP {code}"),
        };
}
