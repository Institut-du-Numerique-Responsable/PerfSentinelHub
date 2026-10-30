using System.Text.Json;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapViewsTests
{
    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static RecapPriority Priority(int rank, string? label = "Showing a cart")
    {
        return new RecapPriority(
            rank, "n_plus_one_sql", "warning", "svc", "plain", 3, 40, null, false, true, "flat", 0.0, label, null,
            new RecapAction("done", "ana", "Ana", 1, 2, "confirmed",
                [new RecapActionEvent(null, "done", "ana", "Ana", "me", 2)]),
            $"sig-{rank}", "GET /x", "SELECT ?", "batch it", new RecapFix("do it", "ef", "https://e.x"),
            new RecapCodeLocation("F", "a.cs", 3, "N"), "daemon_production");
    }

    private static RecapMove Move(string? label = "Placing an order")
    {
        return new RecapMove("n_plus_one_sql", "svc", "plain", "up", null, label, null, "sig-m", "GET /m");
    }

    private static RecapSnapshot Tech(int priorities)
    {
        var period = new RecapPeriodInfo("week", "2026-10-26", "2026-11-01", true, [], [], 1.0, null);
        return new RecapSnapshot(
            1, "tech", 0, period, period, new RecapFilters(null, null, null, null), true,
            new RecapTiles(1, 1, null, null, 0, null, priorities, 0, 0, new RecapActionCounts(0, 0, 0, 0, 0, 0)),
            [.. Enumerable.Range(1, priorities).Select(rank => Priority(rank))], 0,
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
    public void Pilotage_keeps_the_endpoint_only_without_a_label()
    {
        var tech = Tech(2) with
        {
            Priorities = [Priority(1), Priority(2, null)],
            Movements = Tech(2).Movements with { New = [Move(), Move(null)] }
        };

        var view = RecapViews.Pilotage(tech);

        Assert.Null(view.Priorities[0].Endpoint);
        Assert.Equal("GET /x", view.Priorities[1].Endpoint);
        Assert.Null(view.Movements.New[0].Endpoint);
        Assert.Equal("GET /m", view.Movements.New[1].Endpoint);
        var json = JsonSerializer.Serialize(view, Wire);
        Assert.Equal(2, json.Split("\"endpoint\"").Length - 1);
        Assert.Contains("\"label\":\"Showing a cart\"", json);
        Assert.Contains("\"label\":null", json);
    }

    [Fact]
    public void Frozen_drops_the_endpoint_of_an_unlabelled_row_too()
    {
        var tech = Tech(2) with
        {
            Priorities = [Priority(1), Priority(2, null)],
            Movements = Tech(2).Movements with { New = [Move(), Move(null)] }
        };

        var view = RecapViews.Frozen(tech);

        Assert.Equal("pilotage", view.View);
        Assert.All(view.Priorities, priority => Assert.Null(priority.Endpoint));
        Assert.All(view.Movements.New, move => Assert.Null(move.Endpoint));
        Assert.Null(view.Priorities[1].Label);
        Assert.Equal("svc", view.Priorities[1].Service);
        var json = JsonSerializer.Serialize(view, Wire);
        Assert.DoesNotContain("\"endpoint\"", json);
        Assert.DoesNotContain("GET /", json);
        Assert.Contains("\"label\":null", json);
    }

    [Fact]
    public void Both_management_views_keep_the_service_beside_its_name()
    {
        var tech = Tech(1) with { Priorities = [Priority(1) with { ServiceLabel = "Payment" }] };
        tech = tech with
        {
            Movements = tech.Movements with { Gone = [Move(null) with { ServiceLabel = "Payment" }] }
        };

        foreach (var view in new[] { RecapViews.Pilotage(tech), RecapViews.Frozen(tech) })
        {
            var json = JsonSerializer.Serialize(view, Wire);
            Assert.Contains("\"service\":\"svc\",", json, StringComparison.Ordinal);
            Assert.Contains("\"service_label\":\"Payment\"", json, StringComparison.Ordinal);
            Assert.Equal("Payment", Assert.Single(view.Movements.Gone).ServiceLabel);
        }

        // Always there, null while nobody has named the service.
        Assert.Contains("\"service_label\":null", JsonSerializer.Serialize(RecapViews.Frozen(Tech(1)), Wire),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Frozen_carries_an_owner_by_name_only()
    {
        var owned = Priority(1) with
        {
            Action = new RecapAction("in_progress", "alice.martin@acme.com", null, 1, null, null, null)
        };

        var view = RecapViews.Frozen(Tech(1) with { Priorities = [owned] });

        var action = view.Priorities[0].Action!;
        Assert.Null(action.Owner);
        Assert.Equal("Alice Martin", action.OwnerDisplay);
        var json = JsonSerializer.Serialize(view, Wire);
        Assert.DoesNotContain("@", json);
        Assert.DoesNotContain("acme", json);
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
