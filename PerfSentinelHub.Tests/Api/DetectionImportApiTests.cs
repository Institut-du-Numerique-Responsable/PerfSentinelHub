using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PerfSentinelHub.Api;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

// The factory's clock reads day 0, so every page here sits on days 0 to 2.
public sealed class DetectionImportApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const string ApiKey = "0123456789abcdef0123456789abcdef"; // gitleaks:allow -- synthetic test credential
    private readonly HttpClient _client = factory.CreateClient();

    private static HttpRequestMessage Request(string? apiKey, JsonObject page, string query = "source_id=test")
    {
        return Request(apiKey, page.ToJsonString(), query);
    }

    private static HttpRequestMessage Request(string? apiKey, string body, string query = "source_id=test")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/detections?" + query)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (apiKey is not null)
            request.Headers.Add("X-API-Key", apiKey);
        return request;
    }

    private static JsonObject Page(string signature = DetectionLedgerJson.Signature)
    {
        return DetectionLedgerJson.Page([DetectionLedgerJson.Count(2, signature)], [DetectionLedgerJson.Day(2)]);
    }

    private long Refusals(string reason)
    {
        return factory.Services.GetRequiredService<DetectionImportMetrics>().Snapshot()
            .Single(entry => entry.Reason == reason).Count;
    }

    private async Task<string?> ScalarAsync(string sql)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await factory.Database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task A_valid_page_is_stored_and_answers_200()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var page = DetectionLedgerJson.Page(
            [DetectionLedgerJson.Count(2, "stored-a"), DetectionLedgerJson.Count(2, "stored-b", 7, null)],
            [DetectionLedgerJson.Day(2)]);
        using var request = Request(ApiKey, page);
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(2, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("rejected").GetInt32());
        Assert.Equal("stored-a 5 40,stored-b 7 -", await ScalarAsync("""
                                                                    SELECT group_concat(signature || ' ' || detections || ' ' || IFNULL(extra_io_ops, '-'), ',')
                                                                    FROM (SELECT * FROM detection_counts
                                                                          WHERE source_id = 'test' AND signature LIKE 'stored-%'
                                                                          ORDER BY signature);
                                                                    """));
        Assert.Equal("1 0", await ScalarAsync(
            $"SELECT pushed || ' ' || polled FROM detection_ledgers WHERE ledger_id = '{DetectionLedgerJson.LedgerId}';"));
    }

    [Fact]
    public async Task A_pushed_day_keeps_its_carbon_fields()
    {
        var page = DetectionLedgerJson.Page(
            [],
            [DetectionLedgerJson.Day(
                2, avoidableGco2: 12.5, avoidableIoOps: 400, avoidableGco2Low: 6.25, totalIoOps: 5200)]);
        using var request = Request(ApiKey, page);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("12.5 6.25 400 5200", await ScalarAsync("""
                                                            SELECT avoidable_gco2 || ' ' || avoidable_gco2_low || ' ' || avoidable_io_ops
                                                              || ' ' || total_io_ops
                                                            FROM detection_days WHERE source_id = 'test' AND day = 2;
                                                            """));
    }

    [Fact]
    public async Task A_meta_only_sealed_page_seals_the_day()
    {
        // The daemon pushes only the rows changed since its last ack, so the midnight seal
        // usually arrives as no rows and the day object alone.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var request = Request(ApiKey, DetectionLedgerJson.Page([], [DetectionLedgerJson.Day(1, true)]));
        using var response = await _client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        Assert.Equal(0, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal("1", await ScalarAsync(
            "SELECT sealed FROM detection_days WHERE source_id = 'test' AND day = 1;"));
    }

    [Fact]
    public async Task A_second_query_parameter_is_a_400()
    {
        using var request = Request(ApiKey, Page(), "source_id=test&extra=1");
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key-wrong-key-wrong-key-wrong")]
    public async Task A_wrong_key_is_a_401(string? apiKey)
    {
        using var request = Request(apiKey, Page("unauthorized"));
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("0", await ScalarAsync(
            "SELECT COUNT(*) FROM detection_counts WHERE signature = 'unauthorized';"));
    }

    [Fact]
    public async Task A_full_import_gate_is_retryable_without_reading_the_body()
    {
        var gate = factory.Services.GetRequiredService<ImportGate>();
        var entered = Enumerable.Range(0, ImportGate.MaxImports).Count(_ => gate.TryEnter());
        try
        {
            using var request = Request(ApiKey, "not json");
            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++) gate.Exit();
        }
    }

    [Fact]
    public async Task A_held_write_lock_answers_503()
    {
        // No public seam holds the write lock, and the route only gives up after WriteGateWait (5 s).
        var cancellationToken = TestContext.Current.CancellationToken;
        var writeGate = (SemaphoreSlim)typeof(HubDatabase)
            .GetField("_writeGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(factory.Database)!;
        var before = Refusals("write_timeout");
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            using var request = Request(ApiKey, Page("held"));
            using var response = await _client.SendAsync(request, cancellationToken);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        }
        finally
        {
            writeGate.Release();
        }

        Assert.Equal(before + 1, Refusals("write_timeout"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM detection_counts WHERE signature = 'held';"));
    }

    [Fact]
    public async Task An_invalid_page_is_a_400_and_counted()
    {
        var invalid = Page("invalid");
        invalid["ledger_id"] = "not-a-guid";
        var before = Refusals("bad_request");

        using var request = Request(ApiKey, invalid);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before + 1, Refusals("bad_request"));
        Assert.Equal("0", await ScalarAsync("SELECT COUNT(*) FROM detection_counts WHERE signature = 'invalid';"));
    }

    // A lone surrogate is valid JSON text, but no .NET string holds it: reading it throws.
    [Theory]
    [InlineData("signature")]
    [InlineData("ledger_id")]
    public async Task A_lone_surrogate_is_a_400_and_counted(string field)
    {
        var page = Page(field == "signature" ? "surrogate" : DetectionLedgerJson.Signature);
        if (field == "ledger_id")
            page["ledger_id"] = "surrogate";
        var body = page.ToJsonString().Replace("\"surrogate\"", "\"\\ud800\"", StringComparison.Ordinal);
        var before = Refusals("bad_request");

        using var request = Request(ApiKey, body);
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before + 1, Refusals("bad_request"));
    }
}
