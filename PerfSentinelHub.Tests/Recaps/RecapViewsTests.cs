using System.Text.Json;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapViewsTests
{
    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static RecapPriority Priority(int rank)
    {
        return new RecapPriority(
            rank, "n_plus_one_sql", "warning", "svc", "plain", 3, 40, null, false, true, "flat", 0.0,
            new RecapAction("done", "ana", 1, 2, "confirmed", [new RecapActionEvent(null, "done", "ana", "me", 2)]),
            $"sig-{rank}", "GET /x", "SELECT ?", "batch it", new RecapFix("do it", "ef", "https://e.x"),
            new RecapCodeLocation("F", "a.cs", 3, "N"), "daemon_production");
    }

    private static RecapMove Move()
    {
        return new RecapMove("n_plus_one_sql", "svc", "plain", "up", null, "sig-m", "GET /m");
    }

    private static RecapSnapshot Tech(int priorities)
    {
        var period = new RecapPeriodInfo("week", "2026-10-26", "2026-11-01", true, [], [], 1.0, null);
        return new RecapSnapshot(
            1, "tech", 0, period, period, new RecapFilters(null, null, null, null), true,
            new RecapTiles(1, 1, null, null, 0, null, priorities, 0, 0, new RecapActionCounts(0, 0, 0, 0, 0, 0)),
            [.. Enumerable.Range(1, priorities).Select(Priority)], 0,
            new RecapMovements([Move()], [Move()], 0, [Move()], [Move()], [Move()], [Move()]), false, [], false);
    }

    [Fact]
    public void Pilotage_json_carries_no_tech_key()
    {
        var json = JsonSerializer.Serialize(RecapViews.Pilotage(Tech(3)), Wire);

        Assert.Contains("\"view\":\"pilotage\"", json);
        foreach (var key in new[]
                 {
                     "signature", "endpoint", "template", "suggestion", "suggested_fix", "code_location",
                     "confidence", "events"
                 })
            Assert.DoesNotContain($"\"{key}\"", json);
    }

    [Fact]
    public void Pilotage_keeps_five_and_counts_the_rest()
    {
        var view = RecapViews.Pilotage(Tech(12));

        Assert.Equal(5, view.Priorities.Count);
        Assert.Equal(7, view.MorePriorities);
        Assert.Equal(0, RecapViews.Pilotage(Tech(3)).MorePriorities);
    }

    [Fact]
    public void Tech_keeps_everything()
    {
        var json = JsonSerializer.Serialize(Tech(2), Wire);

        foreach (var key in new[]
                 {
                     "signature", "endpoint", "template", "suggestion", "suggested_fix", "code_location",
                     "confidence", "events"
                 })
            Assert.Contains($"\"{key}\"", json);
    }
}
