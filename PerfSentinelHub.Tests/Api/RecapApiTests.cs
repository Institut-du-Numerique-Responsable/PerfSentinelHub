using System.Net;
using System.Text.Json;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Tests.Support;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     GET /api/recap on the factory's clock, 10 s past the epoch: day 0, 1970-01-01. What each caller
///     reads is AccessRoutesTests' business, and the arithmetic RecapServiceTests'.
/// </summary>
public sealed class RecapApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recap_answers_a_snapshot_for_today()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/recap?period=day", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("tech", root.GetProperty("view").GetString());
        var period = root.GetProperty("period");
        Assert.Equal("1970-01-01", period.GetProperty("from").GetString());
        Assert.True(period.GetProperty("in_progress").GetBoolean());
    }

    [Theory]
    [InlineData("period=year")]
    [InlineData("at=2026-13-01")]
    [InlineData("at=1970-01-02")]
    [InlineData("view=other")]
    [InlineData("environment=nowhere")]
    [InlineData("period=day&period=week")]
    public async Task A_bad_query_is_a_400(string query)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/recap?{query}", Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_pilotage_view_carries_no_tech_key()
    {
        const string signature = "n_plus_one_sql:order-svc:recap-pilotage";
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("test", "Test", "test", "0.26.2"),
            FindingJson.Batch(FindingJson.Envelope(signature)), 5_000, Token);
        using var client = factory.CreateClient();

        using var tech = await client.GetAsync("/api/recap?period=day", Token);
        using var pilotage = await client.GetAsync("/api/recap?period=day&view=pilotage", Token);

        var techText = await tech.Content.ReadAsStringAsync(Token);
        var pilotageText = await pilotage.Content.ReadAsStringAsync(Token);
        Assert.Contains(signature, techText);
        Assert.Contains("\"plain\"", pilotageText);
        Assert.Contains("\"view\":\"pilotage\"", pilotageText);
        foreach (var key in new[]
                 {
                     "signature", "endpoint", "template", "suggestion", "suggested_fix", "code_location",
                     "confidence", "events"
                 })
            Assert.DoesNotContain($"\"{key}\"", pilotageText);
    }
}
