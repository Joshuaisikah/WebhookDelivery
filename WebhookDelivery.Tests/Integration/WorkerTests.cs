using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using WebhookDelivery.Infrastructure.Services;
using Xunit.Abstractions;

namespace WebhookDelivery.Tests.Integration;

// Worker tests get their OWN database: the claim query takes any pending row in
// the table, so rows left by the API/constraint tests would otherwise be claimed
// (and POSTed to example.com). xUnit gives each collection its own fixture instance.
[CollectionDefinition(Name)]
public sealed class WorkerCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "worker";
}

// A local HTTP endpoint the worker delivers to. Records every hit by Webhook-Id.
public sealed class FakeReceiver : IAsyncDisposable
{
    private WebApplication _app = null!;
    private int _hangOnceUsed;

    public string BaseUrl { get; private set; } = "";
    public ConcurrentDictionary<string, int> HitsById { get; } = new();
    public ConcurrentQueue<(string Id, string Attempt)> Log { get; } = new();
    public TaskCompletionSource HangHit { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int TotalHits => Log.Count;

    public async Task StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        _app = b.Build();

        _app.MapPost("/ok/{n}", (HttpContext ctx) => Record(ctx, 200));
        _app.MapPost("/status/{code:int}", (HttpContext ctx, int code) => Record(ctx, code));
        // First request hangs forever (until the client goes away); later ones succeed.
        _app.MapPost(
            "/hang-once",
            async (HttpContext ctx) =>
            {
                Record(ctx, 0);
                if (Interlocked.Exchange(ref _hangOnceUsed, 1) == 0)
                {
                    HangHit.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ctx.RequestAborted); }
                    catch (OperationCanceledException) { }
                    return Results.StatusCode(500);
                }
                return Results.StatusCode(200);
            }
        );

        await _app.StartAsync();
        BaseUrl = _app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
    }

    private IResult Record(HttpContext ctx, int code)
    {
        var id = ctx.Request.Headers["Webhook-Id"].ToString();
        HitsById.AddOrUpdate(id, 1, (_, n) => n + 1);
        Log.Enqueue((id, ctx.Request.Headers["Webhook-Attempt"].ToString()));
        return Results.StatusCode(code);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

[Collection(WorkerCollection.Name)]
public sealed class WorkerTests(PostgresApiFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private readonly FakeReceiver _rx = new();
    private readonly HttpClient _api = fx.Factory.CreateClient();

    public async Task InitializeAsync()
    {
        await _rx.StartAsync();
        await ExecAsync("TRUNCATE deliveries, events, webhooks, applications CASCADE");
    }

    public async Task DisposeAsync() => await _rx.DisposeAsync();

    // ---------------- helpers ----------------

    private async Task<int> ExecAsync(string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps)
            cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string, object)[] ps)
    {
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in ps)
            cmd.Parameters.AddWithValue(n, v);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> SeedAppAsync() =>
        await ScalarAsync<Guid>(
            "INSERT INTO applications (id, name, created_at) VALUES (gen_random_uuid(), 'app', now()) RETURNING id"
        );

    // Webhooks are created a minute in the past so fan-out's created_at rule includes them.
    private async Task SeedWebhooksAsync(Guid app, int count, string pathPrefix) =>
        await ExecAsync(
            """
            INSERT INTO webhooks (id, application_id, url, is_active, created_at)
            SELECT gen_random_uuid(), @app, @prefix || g, true, now() - interval '1 minute'
            FROM generate_series(1, @n) g
            """,
            ("app", app),
            ("prefix", _rx.BaseUrl + pathPrefix),
            ("n", count)
        );

    private async Task<Guid> SeedWebhookAsync(Guid app, string path) =>
        await ScalarAsync<Guid>(
            """
            INSERT INTO webhooks (id, application_id, url, is_active, created_at)
            VALUES (gen_random_uuid(), @app, @url, true, now() - interval '1 minute') RETURNING id
            """,
            ("app", app),
            ("url", _rx.BaseUrl + path)
        );

    private async Task<Guid> SeedEventAsync(Guid app, string type = "order.created") =>
        await ScalarAsync<Guid>(
            """
            INSERT INTO events (id, application_id, type, payload, created_at)
            VALUES (gen_random_uuid(), @app, @type, '{"a":1}'::jsonb, now()) RETURNING id
            """,
            ("app", app),
            ("type", type)
        );

    private static DeliveryOptions Opts(Action<DeliveryOptions>? f = null)
    {
        var o = new DeliveryOptions { BackoffBaseSeconds = 2, LeaseSeconds = 60, HttpTimeoutSeconds = 5 };
        f?.Invoke(o);
        return o;
    }

    private DeliveryStore NewStore(DeliveryOptions o) => new(fx.NewDbContext(), Options.Create(o));

    private static DeliverySender NewSender(DeliveryOptions o) =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(o.HttpTimeoutSeconds) }, Options.Create(o));

    private async Task<FanOutResult> FanOutAsync() =>
        await new FanOutService(fx.NewDbContext()).RunOnceAsync(CancellationToken.None);

    // One in-process worker cycle over the delivery phase: claim → send → fenced record.
    private async Task<int> DeliverDueOnceAsync(DeliveryOptions o)
    {
        var batch = await NewStore(o).ClaimBatchAsync(CancellationToken.None);
        var sender = NewSender(o);
        foreach (var d in batch)
        {
            var outcome = await sender.SendAsync(d, CancellationToken.None);
            await NewStore(o).RecordAsync(d, outcome, CancellationToken.None);
        }
        return batch.Count;
    }

    private Task MakeAllDueNowAsync() =>
        ExecAsync("UPDATE deliveries SET next_attempt_at = now() WHERE status = 'pending'");

    private static string PrivateConst(Type t, string name) =>
        (string)t.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    // ---------------- fan-out ----------------

    [Fact]
    public async Task FanOut_run_twice_gives_exactly_5000_delivery_rows()
    {
        var app = await SeedAppAsync();
        await SeedWebhooksAsync(app, 5000, "/ok/");
        await SeedEventAsync(app);

        (await FanOutAsync()).DeliveriesCreated.Should().Be(5000);

        // Simulate a crash-and-rerun: the event looks un-fanned-out again.
        await ExecAsync("UPDATE events SET fan_out_completed_at = NULL");
        var second = await FanOutAsync();

        second.DeliveriesCreated.Should().Be(0);
        (await ScalarAsync<long>("SELECT count(*) FROM deliveries")).Should().Be(5000);
    }

    [Fact]
    public async Task FanOut_two_workers_at_the_same_time_gives_exactly_5000_rows()
    {
        var app = await SeedAppAsync();
        await SeedWebhooksAsync(app, 5000, "/ok/");
        await SeedEventAsync(app);

        var results = await Task.WhenAll(FanOutAsync(), FanOutAsync());

        results.Sum(r => r.DeliveriesCreated).Should().Be(5000);
        results.Count(r => r.ClaimedEvent).Should().Be(1);
        (await ScalarAsync<long>("SELECT count(*) FROM deliveries")).Should().Be(5000);
    }

    [Fact]
    public async Task FanOut_skips_inactive_webhooks_and_webhooks_created_after_the_event()
    {
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, "/ok/1");
        var inactive = await SeedWebhookAsync(app, "/ok/2");
        await ExecAsync("UPDATE webhooks SET is_active = false WHERE id = @id", ("id", inactive));
        await SeedEventAsync(app);
        await ExecAsync(
            "INSERT INTO webhooks (id, application_id, url, is_active, created_at) VALUES (gen_random_uuid(), @app, 'https://late.example', true, now() + interval '1 minute')",
            ("app", app)
        );

        (await FanOutAsync()).DeliveriesCreated.Should().Be(1);
    }

    [Fact]
    public async Task PostEvent_with_5000_webhooks_returns_under_200ms_and_creates_no_deliveries()
    {
        var res = await _api.PostAsJsonAsync("/api/applications", new { name = "big" });
        var app = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await SeedWebhooksAsync(app, 5000, "/ok/");

        for (var i = 0; i < 3; i++) // warm-up: JIT, connection pool
            await _api.PostAsJsonAsync($"/api/applications/{app}/events", new { type = "warm", payload = new { } });

        var times = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await _api.PostAsJsonAsync(
                $"/api/applications/{app}/events",
                new { type = "order.created", payload = new { i } }
            );
            sw.Stop();
            r.IsSuccessStatusCode.Should().BeTrue();
            times.Add(sw.ElapsedMilliseconds);
        }
        output.WriteLine($"POST /events ms: {string.Join(", ", times)}");

        times.Max().Should().BeLessThan(200);
        (await ScalarAsync<long>("SELECT count(*) FROM deliveries")).Should().Be(0, "fan-out must not run in the request");
    }

    // ---------------- retries ----------------

    [Fact]
    public async Task Endpoint_500_is_retried_with_exponential_backoff_up_to_max_then_failed()
    {
        var o = Opts(x => { x.MaxAttempts = 4; x.BackoffBaseSeconds = 2; });
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, "/status/500");
        await SeedEventAsync(app);
        await FanOutAsync();

        var delays = new List<double>();
        for (var round = 0; round < 10; round++)
        {
            if (await DeliverDueOnceAsync(o) == 0)
                break;
            var status = await ScalarAsync<string>("SELECT status FROM deliveries");
            if (status != "pending")
                break;
            delays.Add(await ScalarAsync<double>(
                "SELECT extract(epoch FROM next_attempt_at - updated_at)::float8 FROM deliveries"));

            if (round == 0) // backoff is honoured: not due again straight away
                (await DeliverDueOnceAsync(o)).Should().Be(0);
            await MakeAllDueNowAsync();
        }
        output.WriteLine($"delays: {string.Join(", ", delays.Select(d => d.ToString("F2")))}");

        _rx.TotalHits.Should().Be(4);
        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("failed");
        (await ScalarAsync<int>("SELECT attempt_count FROM deliveries")).Should().Be(4);
        (await ScalarAsync<int>("SELECT last_status_code FROM deliveries")).Should().Be(500);

        // base 2s, jitter 50–100%: attempt n waits within [2^n * 0.5, 2^n] seconds
        delays.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
            delays[i].Should().BeInRange(2 * Math.Pow(2, i) * 0.5 - 0.05, 2 * Math.Pow(2, i) + 0.05);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(301)]
    public async Task Endpoint_4xx_or_redirect_is_not_retried(int code)
    {
        var o = Opts();
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, $"/status/{code}");
        await SeedEventAsync(app);
        await FanOutAsync();

        (await DeliverDueOnceAsync(o)).Should().Be(1);
        await MakeAllDueNowAsync();
        (await DeliverDueOnceAsync(o)).Should().Be(0);

        _rx.TotalHits.Should().Be(1);
        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("failed");
        (await ScalarAsync<int>("SELECT attempt_count FROM deliveries")).Should().Be(1);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Endpoint_408_429_5xx_stays_pending_for_retry(int code)
    {
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, $"/status/{code}");
        await SeedEventAsync(app);
        await FanOutAsync();

        await DeliverDueOnceAsync(Opts());

        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("pending");
    }

    [Fact]
    public async Task Unreachable_endpoint_is_retried_not_failed()
    {
        var app = await SeedAppAsync();
        // Port 1 on loopback: connection refused.
        await ExecAsync(
            "INSERT INTO webhooks (id, application_id, url, is_active, created_at) VALUES (gen_random_uuid(), @app, 'http://127.0.0.1:1/x', true, now() - interval '1 minute')",
            ("app", app)
        );
        await SeedEventAsync(app);
        await FanOutAsync();

        await DeliverDueOnceAsync(Opts());

        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("pending");
        (await ScalarAsync<string>("SELECT last_error FROM deliveries")).Should().StartWith("network");
    }

    // A delivery whose send THROWS (instead of returning an outcome) is a poison
    // message: the batch aborts, the row is reclaimed after every lease forever,
    // attempt_count climbs past MaxAttempts, and it never reaches 'failed'.
    [Fact]
    public async Task Event_type_with_a_newline_does_not_make_the_sender_throw()
    {
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, "/ok/1");
        await SeedEventAsync(app, "order\ncreated");
        await FanOutAsync();

        var o = Opts();
        var batch = await NewStore(o).ClaimBatchAsync(CancellationToken.None);
        var act = () => NewSender(o).SendAsync(batch.Single(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Api_rejects_event_type_that_cannot_be_sent_as_a_header()
    {
        var res = await _api.PostAsJsonAsync("/api/applications", new { name = "x" });
        var app = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var r = await _api.PostAsJsonAsync($"/api/applications/{app}/events", new { type = "order\ncreated", payload = new { } });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a type that cannot be sent as a header should be rejected at the door");
    }

    // ---------------- claims, leases, fencing ----------------

    [Fact]
    public async Task Two_claims_at_once_never_get_the_same_delivery()
    {
        var o = Opts(x => x.BatchSize = 50);
        var app = await SeedAppAsync();
        await SeedWebhooksAsync(app, 200, "/ok/");
        await SeedEventAsync(app);
        await FanOutAsync();

        var batches = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewStore(o).ClaimBatchAsync(CancellationToken.None)));
        var ids = batches.SelectMany(b => b.Select(d => d.Id)).ToList();

        ids.Should().HaveCount(200).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Hung_worker_cannot_write_its_result_after_its_claim_was_reclaimed()
    {
        var o = Opts(x => x.LeaseSeconds = 1);
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, "/ok/1");
        await SeedEventAsync(app);
        await FanOutAsync();

        var a = (await NewStore(o).ClaimBatchAsync(CancellationToken.None)).Single(); // worker A claims, then hangs
        await Task.Delay(1500); // A's lease expires
        var b = (await NewStore(o).ClaimBatchAsync(CancellationToken.None)).Single(); // worker B reclaims
        b.Id.Should().Be(a.Id);
        b.ClaimToken.Should().NotBe(a.ClaimToken);

        // A wakes up before B finishes and tries to write: fenced out.
        (await NewStore(o).RecordAsync(a, RetryPolicy.Permanent(400, "A's stale result"), CancellationToken.None))
            .Should().BeFalse();

        // B's write still lands.
        var outcome = await NewSender(o).SendAsync(b, CancellationToken.None);
        (await NewStore(o).RecordAsync(b, outcome, CancellationToken.None)).Should().BeTrue();

        // A tries again after B finished: still fenced out.
        (await NewStore(o).RecordAsync(a, RetryPolicy.Permanent(400, "A again"), CancellationToken.None))
            .Should().BeFalse();

        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("delivered");
        (await ScalarAsync<int>("SELECT attempt_count FROM deliveries")).Should().Be(2);
    }

    [Fact]
    public async Task Claim_query_uses_ix_deliveries_claim_EXPLAIN()
    {
        var app = await SeedAppAsync();
        var hook = await SeedWebhookAsync(app, "/ok/1");
        // 20,000 finished deliveries + 50 pending: the partial index should be the obvious choice.
        await ExecAsync(
            """
            WITH ev AS (
                INSERT INTO events (id, application_id, type, payload, created_at, fan_out_completed_at)
                SELECT gen_random_uuid(), @app, 't', '{}'::jsonb, now(), now() FROM generate_series(1, 20050)
                RETURNING id
            )
            INSERT INTO deliveries (id, application_id, webhook_id, event_id, status, attempt_count, next_attempt_at, created_at, updated_at)
            SELECT gen_random_uuid(), @app, @hook, ev.id,
                   CASE WHEN row_number() OVER () <= 50 THEN 'pending' ELSE 'delivered' END,
                   1, now(), now(), now()
            FROM ev
            """,
            ("app", app),
            ("hook", hook)
        );
        await ExecAsync("ANALYZE");

        var claimSql = PrivateConst(typeof(DeliveryStore), "ClaimSql");
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("EXPLAIN " + claimSql, conn);
        cmd.Parameters.AddWithValue("batch", 20);
        cmd.Parameters.AddWithValue("lease", 60d);
        cmd.Parameters.AddWithValue("token", Guid.NewGuid());
        var lines = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                lines.Add(r.GetString(0));
        var plan = string.Join('\n', lines);
        output.WriteLine(plan);

        plan.Should().Contain("ix_deliveries_claim");
    }

    // ---------------- deactivation ----------------

    private async Task<(Guid app, Guid hook)> AppWithHookViaApiAsync(string path = "/ok/1")
    {
        var res = await _api.PostAsJsonAsync("/api/applications", new { name = "d" });
        var app = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var hook = await SeedWebhookAsync(app, path);
        return (app, hook);
    }

    private Task<HttpResponseMessage> PatchAsync(Guid app, Guid hook, object body) =>
        _api.PatchAsJsonAsync($"/api/applications/{app}/webhooks/{hook}", body);

    [Fact]
    public async Task Deactivating_a_webhook_cancels_its_pending_deliveries_and_none_are_sent()
    {
        var (app, hook) = await AppWithHookViaApiAsync();
        for (var i = 0; i < 3; i++)
        {
            await SeedEventAsync(app);
            await FanOutAsync();
        }

        var res = await PatchAsync(app, hook, new { isActive = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isActive").GetBoolean().Should().BeFalse();
        body.GetProperty("cancelledDeliveries").GetInt32().Should().Be(3);

        (await DeliverDueOnceAsync(Opts())).Should().Be(0);
        _rx.TotalHits.Should().Be(0);
        (await ScalarAsync<long>("SELECT count(*) FROM deliveries WHERE status = 'cancelled'")).Should().Be(3);
    }

    [Fact]
    public async Task Deactivating_while_a_delivery_is_in_flight_fences_out_the_worker_result()
    {
        var (app, hook) = await AppWithHookViaApiAsync();
        await SeedEventAsync(app);
        await FanOutAsync();

        var o = Opts();
        var d = (await NewStore(o).ClaimBatchAsync(CancellationToken.None)).Single(); // worker claimed it
        await PatchAsync(app, hook, new { isActive = false }); // deactivated mid-flight
        var outcome = await NewSender(o).SendAsync(d, CancellationToken.None); // the HTTP call still happens

        (await NewStore(o).RecordAsync(d, outcome, CancellationToken.None)).Should().BeFalse();
        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("cancelled");
    }

    // Fan-out (transaction F) has inserted pending rows but not committed yet when the
    // PATCH runs. The PATCH's cancel UPDATE cannot see F's uncommitted rows, so it
    // cancels 0 and commits. F commits. Result: a 'pending' delivery for an inactive
    // webhook, which the claim filter hides — until someone reactivates the webhook.
    [Fact]
    public async Task Deactivate_racing_an_uncommitted_fan_out_leaves_no_pending_rows_behind()
    {
        var (app, hook) = await AppWithHookViaApiAsync();
        await SeedEventAsync(app);

        await using var f = new NpgsqlConnection(fx.ConnectionString);
        await f.OpenAsync();
        await using var tx = await f.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(PrivateConst(typeof(FanOutService), "Sql"), f, tx))
            await cmd.ExecuteNonQueryAsync(); // F: rows inserted, not committed

        var patch = PatchAsync(app, hook, new { isActive = false });
        (await Task.WhenAny(patch, Task.Delay(5000))).Should().Be(patch, "PATCH should not block on the fan-out");
        (await (await patch).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("cancelledDeliveries").GetInt32().Should().Be(0);

        await tx.CommitAsync(); // F commits

        (await ScalarAsync<long>(
            "SELECT count(*) FROM deliveries d JOIN webhooks w ON w.id = d.webhook_id WHERE d.status = 'pending' AND NOT w.is_active"))
            .Should().Be(0, "an inactive webhook should have no pending deliveries waiting to fire on reactivation");
    }

    [Fact]
    public async Task Reactivating_does_not_resurrect_cancelled_deliveries()
    {
        var (app, hook) = await AppWithHookViaApiAsync();
        await SeedEventAsync(app);
        await FanOutAsync();
        await PatchAsync(app, hook, new { isActive = false });

        var res = await PatchAsync(app, hook, new { isActive = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DeliverDueOnceAsync(Opts())).Should().Be(0);
        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("cancelled");
    }

    [Fact]
    public async Task Patch_webhook_of_another_application_returns_404_and_changes_nothing()
    {
        var (_, hook) = await AppWithHookViaApiAsync();
        var (otherApp, _) = await AppWithHookViaApiAsync();

        var res = await PatchAsync(otherApp, hook, new { isActive = false });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ScalarAsync<bool>("SELECT is_active FROM webhooks WHERE id = @id", ("id", hook))).Should().BeTrue();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"isActive\":null}")]
    [InlineData("{\"isActive\":\"no\"}")]
    public async Task Patch_webhook_without_a_bool_isActive_returns_400(string json)
    {
        var (app, hook) = await AppWithHookViaApiAsync();
        var res = await _api.PatchAsync(
            $"/api/applications/{app}/webhooks/{hook}",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        );
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------- the real Worker process ----------------

    private static string WorkerDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebhookDelivery.slnx")))
            dir = dir.Parent;
        var config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        return Path.Combine(dir!.FullName, "WebhookDelivery.Worker", "bin", config, "net10.0", "WebhookDelivery.Worker.dll");
    }

    private Process StartWorker(bool once, Dictionary<string, string>? extra = null)
    {
        var psi = new ProcessStartInfo("dotnet", $"\"{WorkerDll()}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment["ConnectionStrings__Default"] = fx.ConnectionString;
        psi.Environment["Worker__Once"] = once ? "true" : "false";
        psi.Environment["Worker__PollIntervalMs"] = "100";
        psi.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (k, v) in extra ?? new())
            psi.Environment[k] = v;

        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) output.WriteLine($"[worker {p.Id}] {e.Data}"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.WriteLine($"[worker {p.Id} err] {e.Data}"); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    public static IEnumerable<object[]> TwentyRuns => Enumerable.Range(1, 20).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(TwentyRuns))]
    public async Task Three_worker_processes_1000_pending_each_delivered_exactly_once(int run)
    {
        var app = await SeedAppAsync();
        await SeedWebhooksAsync(app, 1000, "/ok/");
        await SeedEventAsync(app);
        (await FanOutAsync()).DeliveriesCreated.Should().Be(1000);

        var workers = Enumerable.Range(0, 3).Select(_ => StartWorker(once: true)).ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Task.WhenAll(workers.Select(w => w.WaitForExitAsync(cts.Token)));

        output.WriteLine($"run {run}: exit codes {string.Join(",", workers.Select(w => w.ExitCode))}");
        workers.Should().AllSatisfy(w => w.ExitCode.Should().Be(0));
        (await ScalarAsync<long>("SELECT count(*) FROM deliveries WHERE status = 'delivered'")).Should().Be(1000);
        (await ScalarAsync<long>("SELECT count(*) FROM deliveries WHERE attempt_count <> 1")).Should().Be(0);
        _rx.HitsById.Should().HaveCount(1000);
        _rx.HitsById.Values.Should().AllSatisfy(n => n.Should().Be(1));
    }

    [Fact]
    public async Task Kill_9_mid_delivery_is_retried_after_the_lease_not_lost_not_doubled()
    {
        var app = await SeedAppAsync();
        await SeedWebhookAsync(app, "/hang-once");
        var ev = await SeedEventAsync(app);
        await FanOutAsync();

        var settings = new Dictionary<string, string>
        {
            ["Delivery__LeaseSeconds"] = "3",
            ["Delivery__HttpTimeoutSeconds"] = "2",
            ["Delivery__BatchSize"] = "1",
            ["Delivery__Concurrency"] = "1",
        };
        var w1 = StartWorker(once: false, settings);
        (await Task.WhenAny(_rx.HangHit.Task, Task.Delay(30_000))).Should().Be(_rx.HangHit.Task, "worker 1 should reach the endpoint");

        w1.Kill(); // SIGKILL on Linux: no finally blocks, no graceful shutdown
        await w1.WaitForExitAsync();

        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("pending");
        (await ScalarAsync<bool>("SELECT locked_until > now() FROM deliveries")).Should().BeTrue("the dead worker's lease is still held");

        await Task.Delay(3500); // let the lease expire
        var w2 = StartWorker(once: true, settings);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await w2.WaitForExitAsync(cts.Token);

        (await ScalarAsync<long>("SELECT count(*) FROM deliveries WHERE event_id = @e", ("e", ev))).Should().Be(1);
        (await ScalarAsync<string>("SELECT status FROM deliveries")).Should().Be("delivered");
        (await ScalarAsync<int>("SELECT attempt_count FROM deliveries")).Should().Be(2);
        // At-least-once: the receiver saw the same Webhook-Id twice (the first never got a response).
        _rx.HitsById.Should().ContainSingle().Which.Value.Should().Be(2);
    }
}
