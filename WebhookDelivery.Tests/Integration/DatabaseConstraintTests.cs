using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WebhookDelivery.Infrastructure.Data;

namespace WebhookDelivery.Tests.Integration;

// The schema is the last line of defence: these go straight at the database
// (via EF and raw SQL), bypassing the API, to prove each constraint is real.
[Collection(PostgresCollection.Name)]
public sealed class DatabaseConstraintTests(PostgresApiFixture fx)
{
    private async Task<(Application app, Webhook hook, Event ev)> SeedAsync(AppDbContext db, string name = "a")
    {
        var app = new Application { Name = name };
        var hook = new Webhook { ApplicationId = app.Id, Url = "https://example.com/h" };
        var ev = new Event { ApplicationId = app.Id, Type = "t", Payload = "{}" };
        db.AddRange(app, hook, ev);
        await db.SaveChangesAsync();
        return (app, hook, ev);
    }

    private static string SqlState(Exception ex) =>
        (ex.InnerException as PostgresException ?? ex as PostgresException)?.SqlState ?? "none";

    [Fact]
    public async Task Migration_matches_the_model_no_pending_changes()
    {
        await using var db = fx.NewDbContext();
        db.Database.HasPendingModelChanges().Should().BeFalse("the model changed without a new migration");
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Delivery_valid_row_saves_and_status_is_stored_lowercase()
    {
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        var d = new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id };
        db.Deliveries.Add(d);
        await db.SaveChangesAsync();

        var status = await db.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM deliveries WHERE id = {d.Id}")
            .SingleAsync();
        status.Should().Be("pending");
    }

    [Fact]
    public async Task Delivery_duplicate_app_webhook_event_is_rejected_23505()
    {
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        db.Deliveries.Add(new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id });
        await db.SaveChangesAsync();

        await using var db2 = fx.NewDbContext();
        db2.Deliveries.Add(new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id });
        var act = () => db2.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        SqlState(ex.Which).Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Delivery_webhook_from_another_application_is_rejected_23503()
    {
        await using var db = fx.NewDbContext();
        var (appA, _, evA) = await SeedAsync(db, "A");
        var (_, hookB, _) = await SeedAsync(db, "B");

        await using var db2 = fx.NewDbContext();
        db2.Deliveries.Add(new Delivery { ApplicationId = appA.Id, WebhookId = hookB.Id, EventId = evA.Id });
        var act = () => db2.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        SqlState(ex.Which).Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Delivery_event_from_another_application_is_rejected_23503()
    {
        await using var db = fx.NewDbContext();
        var (appA, hookA, _) = await SeedAsync(db, "A");
        var (_, _, evB) = await SeedAsync(db, "B");

        await using var db2 = fx.NewDbContext();
        db2.Deliveries.Add(new Delivery { ApplicationId = appA.Id, WebhookId = hookA.Id, EventId = evB.Id });
        var act = () => db2.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        SqlState(ex.Which).Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Delivery_bad_status_is_rejected_by_check_constraint_23514()
    {
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        var act = () =>
            db.Database.ExecuteSqlAsync(
                $"INSERT INTO deliveries (id, application_id, webhook_id, event_id, status, attempt_count, next_attempt_at, created_at, updated_at) VALUES ({Guid.NewGuid()}, {app.Id}, {hook.Id}, {ev.Id}, 'exploded', 0, now(), now(), now())"
            );

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Delivery_negative_attempt_count_is_rejected_23514()
    {
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        db.Deliveries.Add(
            new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id, AttemptCount = -1 }
        );
        var act = () => db.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        SqlState(ex.Which).Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Deleting_event_cascades_its_deliveries()
    {
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        var d = new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id };
        db.Deliveries.Add(d);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"DELETE FROM events WHERE id = {ev.Id}");

        (await db.Deliveries.AsNoTracking().AnyAsync(x => x.Id == d.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_webhook_with_deliveries_is_restricted_23503()
    {
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        db.Deliveries.Add(new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id });
        await db.SaveChangesAsync();

        var act = () => db.Database.ExecuteSqlAsync($"DELETE FROM webhooks WHERE id = {hook.Id}");

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Deleting_application_with_deliveries_does_not_crash_mid_cascade()
    {
        // applications → webhooks is CASCADE, but webhooks → deliveries is RESTRICT.
        // Deleting an app that has deliveries: does Postgres cascade events (and their
        // deliveries) first, or hit the webhook RESTRICT and refuse? Pin down which.
        await using var db = fx.NewDbContext();
        var (app, hook, ev) = await SeedAsync(db);
        db.Deliveries.Add(new Delivery { ApplicationId = app.Id, WebhookId = hook.Id, EventId = ev.Id });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlAsync($"DELETE FROM applications WHERE id = {app.Id}");

        (await db.Applications.AsNoTracking().AnyAsync(a => a.Id == app.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Partial_indexes_exist_with_their_filters()
    {
        await using var db = fx.NewDbContext();
        var defs = await db.Database
            .SqlQuery<string>(
                $"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname IN ('ix_webhooks_application_active','ix_events_pending_fanout','ix_deliveries_claim','ux_deliveries_app_webhook_event') ORDER BY indexname"
            )
            .ToListAsync();

        defs.Should().HaveCount(4);
        defs.Should().Contain(d => d.Contains("ix_deliveries_claim") && d.Contains("WHERE") && d.Contains("pending"));
        defs.Should().Contain(d => d.Contains("ix_events_pending_fanout") && d.Contains("fan_out_completed_at IS NULL"));
        defs.Should().Contain(d => d.Contains("ix_webhooks_application_active") && d.Contains("WHERE is_active"));
        defs.Should().Contain(d => d.Contains("ux_deliveries_app_webhook_event") && d.Contains("UNIQUE"));
    }
}
