using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace WebhookDelivery.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class ApplicationEndpointsTests(PostgresApiFixture fx)
{
    private readonly HttpClient _client = fx.Factory.CreateClient();

    private async Task<Guid> CreateAppAsync(string name = "shop")
    {
        var res = await _client.PostAsJsonAsync("/api/applications", new { name });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    // ---------- POST /api/applications ----------

    [Fact]
    public async Task CreateApplication_valid_returns_201_with_location_and_persists_trimmed_name()
    {
        var res = await _client.PostAsJsonAsync("/api/applications", new { name = "  shop  " });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("name").GetString().Should().Be("shop");
        res.Headers.Location!.ToString().Should().Be($"/api/applications/{id}");

        await using var db = fx.NewDbContext();
        var row = await db.Applications.SingleAsync(a => a.Id == id);
        row.Name.Should().Be("shop");
    }

    [Theory]
    [InlineData("{}")] // missing
    [InlineData("{\"name\":null}")]
    [InlineData("{\"name\":\"\"}")]
    public async Task CreateApplication_missing_or_empty_name_returns_400(string json)
    {
        var res = await PostRawAsync("/api/applications", json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateApplication_whitespace_only_name_returns_400_not_an_empty_row()
    {
        // Guards the .Trim() in ApplicationService: if "   " ever got past validation
        // it would be stored as "". [Required] (AllowEmptyStrings = false) rejects it.
        var res = await _client.PostAsJsonAsync("/api/applications", new { name = "   " });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateApplication_name_over_200_chars_returns_400()
    {
        var res = await _client.PostAsJsonAsync("/api/applications", new { name = new string('a', 201) });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateApplication_name_exactly_200_chars_returns_201()
    {
        var res = await _client.PostAsJsonAsync("/api/applications", new { name = new string('a', 200) });
        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateApplication_malformed_json_returns_400_not_500()
    {
        var res = await PostRawAsync("/api/applications", "{\"name\":");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- POST /api/applications/{id}/webhooks ----------

    [Fact]
    public async Task CreateWebhook_valid_returns_201_active_with_secret_and_persists()
    {
        var appId = await CreateAppAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/applications/{appId}/webhooks",
            new { url = "https://example.com/hook" }
        );

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("applicationId").GetGuid().Should().Be(appId);
        body.GetProperty("isActive").GetBoolean().Should().BeTrue();
        body.GetProperty("secret").GetString().Should().StartWith("whsec_");
        res.Headers.Location!.ToString().Should().Be($"/api/applications/{appId}/webhooks/{id}");

        await using var db = fx.NewDbContext();
        (await db.Webhooks.AnyAsync(w => w.Id == id && w.ApplicationId == appId)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateWebhook_secrets_are_unique_per_webhook()
    {
        var appId = await CreateAppAsync();
        var secrets = new HashSet<string>();
        for (var i = 0; i < 20; i++)
        {
            var res = await _client.PostAsJsonAsync(
                $"/api/applications/{appId}/webhooks",
                new { url = $"https://example.com/{i}" }
            );
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            secrets.Add(body.GetProperty("secret").GetString()!);
        }
        secrets.Should().HaveCount(20);
    }

    [Fact]
    public async Task CreateWebhook_unknown_application_returns_404()
    {
        var res = await _client.PostAsJsonAsync(
            $"/api/applications/{Guid.NewGuid()}/webhooks",
            new { url = "https://example.com/hook" }
        );
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task CreateWebhook_non_http_url_returns_400(string url)
    {
        var appId = await CreateAppAsync();
        var res = await _client.PostAsJsonAsync($"/api/applications/{appId}/webhooks", new { url });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateWebhook_missing_url_returns_400()
    {
        var appId = await CreateAppAsync();
        var res = await PostRawAsync($"/api/applications/{appId}/webhooks", "{}");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateWebhook_url_over_2048_chars_returns_400()
    {
        var appId = await CreateAppAsync();
        var url = "https://example.com/" + new string('a', 2048);
        var res = await _client.PostAsJsonAsync($"/api/applications/{appId}/webhooks", new { url });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateWebhook_non_guid_id_returns_404_from_route_constraint()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/applications/not-a-guid/webhooks",
            new { url = "https://example.com/hook" }
        );
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------- POST /api/applications/{id}/events ----------

    [Fact]
    public async Task CreateEvent_valid_returns_202_and_stores_payload_as_jsonb()
    {
        var appId = await CreateAppAsync();

        var res = await PostRawAsync(
            $"/api/applications/{appId}/events",
            "{\"type\":\" order.paid \",\"payload\":{\"orderId\":42,\"total\":9.5}}"
        );

        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();
        body.GetProperty("type").GetString().Should().Be("order.paid");

        await using var db = fx.NewDbContext();
        var ev = await db.Events.SingleAsync(e => e.Id == id);
        ev.ApplicationId.Should().Be(appId);
        ev.FanOutCompletedAt.Should().BeNull("a new event still needs fan-out");

        // Ask Postgres itself: the column must be jsonb and the value queryable as JSON.
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_typeof(payload)::text, payload->>'orderId' FROM events WHERE id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Value = id;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("jsonb");
        reader.GetString(1).Should().Be("42");
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("[1,2,3]")]
    [InlineData("123")]
    [InlineData("true")]
    public async Task CreateEvent_any_json_value_payload_is_accepted(string payload)
    {
        var appId = await CreateAppAsync();
        var res = await PostRawAsync(
            $"/api/applications/{appId}/events",
            $"{{\"type\":\"t\",\"payload\":{payload}}}"
        );
        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task CreateEvent_unknown_application_returns_404()
    {
        var res = await PostRawAsync(
            $"/api/applications/{Guid.NewGuid()}/events",
            "{\"type\":\"t\",\"payload\":{}}"
        );
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("{\"type\":\"t\"}")] // payload missing
    [InlineData("{\"type\":\"t\",\"payload\":null}")]
    [InlineData("{\"payload\":{}}")] // type missing
    [InlineData("{\"type\":\"\",\"payload\":{}}")]
    public async Task CreateEvent_missing_type_or_payload_returns_400(string json)
    {
        var appId = await CreateAppAsync();
        var res = await PostRawAsync($"/api/applications/{appId}/events", json);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_whitespace_only_type_returns_400_not_an_empty_type()
    {
        var appId = await CreateAppAsync();
        var res = await PostRawAsync(
            $"/api/applications/{appId}/events",
            "{\"type\":\"   \",\"payload\":{}}"
        );
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_payload_over_256KiB_returns_413_and_stores_nothing()
    {
        var appId = await CreateAppAsync();
        var big = new string('x', 256 * 1024);
        var res = await PostRawAsync(
            $"/api/applications/{appId}/events",
            $"{{\"type\":\"t\",\"payload\":\"{big}\"}}"
        );

        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        await using var db = fx.NewDbContext();
        (await db.Events.CountAsync(e => e.ApplicationId == appId)).Should().Be(0);
    }

    [Fact]
    public async Task CreateEvent_payload_limit_is_bytes_not_chars()
    {
        // 100k chars of a 3-byte UTF-8 character = ~300 KB on the wire and in jsonb,
        // over a 256 KiB limit, but only 100k .NET chars.
        var appId = await CreateAppAsync();
        var big = new string('€', 100_000);
        var res = await PostRawAsync(
            $"/api/applications/{appId}/events",
            $"{{\"type\":\"t\",\"payload\":\"{big}\"}}"
        );
        res.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task CreateEvent_50_concurrent_posts_all_202_and_all_stored()
    {
        var appId = await CreateAppAsync();

        var tasks = Enumerable
            .Range(0, 50)
            .Select(i =>
                PostRawAsync($"/api/applications/{appId}/events", $"{{\"type\":\"t\",\"payload\":{{\"i\":{i}}}}}")
            );
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Accepted);
        await using var db = fx.NewDbContext();
        (await db.Events.CountAsync(e => e.ApplicationId == appId)).Should().Be(50);
    }

    private Task<HttpResponseMessage> PostRawAsync(string url, string json) =>
        _client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
}
