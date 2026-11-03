using System.Text;
using Microsoft.Extensions.Options;

namespace WebhookDelivery.Infrastructure.Services;

public class DeliverySender(HttpClient http, IOptions<DeliveryOptions> options)
{
    private readonly DeliveryOptions _o = options.Value;

    public async Task<DeliveryOutcome> SendAsync(ClaimedDelivery d, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, d.Url)
            {
                Content = new StringContent(d.Payload, Encoding.UTF8, "application/json")
            };
            // Stable across retries: the receiver dedupes on this.
            req.Headers.Add("Webhook-Id", d.Id.ToString());
            req.Headers.Add("Webhook-Event-Id", d.EventId.ToString());
            req.Headers.Add("Webhook-Event-Type", d.EventType);
            req.Headers.Add("Webhook-Attempt", d.Attempt.ToString());

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return RetryPolicy.FromStatus((int)resp.StatusCode, d.Attempt, _o);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; 
            // shutting down: leave the lease to expire; it will be retried
        }
        catch (OperationCanceledException)
        {
            return RetryPolicy.Retryable(d.Attempt, null, "timeout", _o);
        }
        catch (HttpRequestException ex)
        {
            return RetryPolicy.Retryable(d.Attempt, null, $"network: {ex.Message}", _o);
        }
        catch (UriFormatException ex)
        {
            return RetryPolicy.Permanent(null, $"bad url: {ex.Message}");
        }
    }
}
