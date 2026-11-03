using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Services;

public sealed record ClaimedDelivery(
    Guid Id, Guid ApplicationId, Guid WebhookId, Guid EventId, int Attempt,
    string Url, string? Secret, string EventType, string Payload, Guid ClaimToken);

public class DeliveryStore(AppDbContext db, IOptions<DeliveryOptions> options)
{
    private readonly DeliveryOptions _o = options.Value;

    private const string ClaimSql = """
        WITH picked AS (
            SELECT d.id
            FROM deliveries d
            JOIN webhooks w ON w.id = d.webhook_id AND w.application_id = d.application_id
            WHERE d.status = 'pending'
              AND d.next_attempt_at <= now()
              AND (d.locked_until IS NULL OR d.locked_until < now())
              AND w.is_active
            ORDER BY d.next_attempt_at
            LIMIT @batch
            FOR UPDATE OF d SKIP LOCKED
        )
        UPDATE deliveries d
        SET locked_until  = now() + make_interval(secs => @lease),
            claim_token   = @token,
            attempt_count = d.attempt_count + 1,
            updated_at    = now()
        FROM picked p, webhooks w, events e
        WHERE d.id = p.id
          AND w.id = d.webhook_id
          AND e.id = d.event_id
        RETURNING d.id, d.application_id, d.webhook_id, d.event_id, d.attempt_count,
                  w.url, w.secret, e.type, e.payload::text;
        """;

    // Fenced write: only succeeds if we still hold the claim.
    private const string RecordSql = """
        UPDATE deliveries
        SET status           = @status,
            last_status_code = @code,
            last_error       = @error,
            delivered_at     = CASE WHEN @status = 'delivered' THEN now() ELSE delivered_at END,
            next_attempt_at  = CASE WHEN @status = 'pending'
                                    THEN now() + make_interval(secs => @delay)
                                    ELSE next_attempt_at END,
            locked_until     = NULL,
            claim_token      = NULL,
            updated_at       = now()
        WHERE id = @id AND claim_token = @token AND status = 'pending';
        """;

    public async Task<List<ClaimedDelivery>> ClaimBatchAsync(CancellationToken ct)
    {
        var token = Guid.NewGuid();
        var result = new List<ClaimedDelivery>();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(ClaimSql, conn);
            cmd.Parameters.AddWithValue("batch", _o.BatchSize);
            cmd.Parameters.AddWithValue("lease", (double)_o.LeaseSeconds);
            cmd.Parameters.AddWithValue("token", token);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                result.Add(new ClaimedDelivery(
                    r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetInt32(4),
                    r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetString(7), r.GetString(8), token));
        }
        finally { await db.Database.CloseConnectionAsync(); }
        return result;
    }

    public async Task<bool> RecordAsync(ClaimedDelivery d, DeliveryOutcome o, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(RecordSql, conn);
            cmd.Parameters.AddWithValue("status", o.Status);
            cmd.Parameters.AddWithValue("code", (object?)o.StatusCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("error",
                (object?)(o.Error is { Length: > 2000 } ? o.Error[..2000] : o.Error) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("delay", o.RetryDelaySeconds ?? 0d);
            cmd.Parameters.AddWithValue("id", d.Id);
            cmd.Parameters.AddWithValue("token", d.ClaimToken);
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
