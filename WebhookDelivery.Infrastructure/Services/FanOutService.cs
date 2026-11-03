using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Infrastructure.Services;

public sealed record FanOutResult(bool ClaimedEvent, int DeliveriesCreated);

public class FanOutService(AppDbContext db)
{
    private const string Sql = """
        WITH claimed AS (
            SELECT id, application_id, created_at
            FROM events
            WHERE fan_out_completed_at IS NULL
            ORDER BY created_at
            LIMIT 1
            FOR UPDATE SKIP LOCKED
        ),
        ins AS (
            INSERT INTO deliveries
                (id, application_id, webhook_id, event_id, status,
                 attempt_count, next_attempt_at, created_at, updated_at)
            SELECT gen_random_uuid(), c.application_id, w.id, c.id, 'pending',
                   0, now(), now(), now()
            FROM claimed c
            JOIN webhooks w
              ON w.application_id = c.application_id
             AND w.is_active
             AND w.created_at <= c.created_at
            ON CONFLICT (application_id, webhook_id, event_id) DO NOTHING
            RETURNING 1
        ),
        done AS (
            UPDATE events SET fan_out_completed_at = now()
            WHERE id IN (SELECT id FROM claimed)
            RETURNING 1
        )
        SELECT (SELECT count(*) FROM ins), (SELECT count(*) FROM done);
        """;

    public async Task<FanOutResult> RunOnceAsync(CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(Sql, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return new FanOutResult(r.GetInt64(1) > 0, (int)r.GetInt64(0));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
