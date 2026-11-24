using System.Text.Json;
using PerfSentinelHub.Links;

namespace PerfSentinelHub.Tests.Links;

public sealed class IncidentLinkRulesTests
{
    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // The incident's frozen findings add up to 1000 in the window and 70 000 over the covered days.
    private const long WindowTotal = 1000;
    private const long BaselineTotal = 70_000;
    private static readonly Baseline Unknown = new(0, 0, 0);
    private static readonly Baseline NewFinding = new(7, 0, 0);
    private static readonly Recurrence NoRecurrence = new(0, 0);

    // 100 a day is 1 % of the incident's usual findings: seen 20 of 1000 is ratio 2, seen 100 is ratio 10.
    private static Baseline Daily(long perDay = 100, int seen = 7) => new(7, seen, perDay * 7);

    private static LinkInput Input(string kind, string type, Baseline baseline, long seen = 1,
        Recurrence? recurrence = null, double? saturation = null, bool afterDeploy = false) =>
        new(kind, type, saturation, WindowTotal, BaselineTotal, seen, afterDeploy, baseline, recurrence ?? NoRecurrence);

    public static TheoryData<string, LinkInput, LinkLevel, string[]> Cases => new()
    {
        { "spike", Input("oom_kill", "n_plus_one_sql", new Baseline(7, 7, 700), 300), LinkLevel.Strong,
            ["mechanism_compatible", "intensity"] },
        { "chronic", Input("oom_kill", "n_plus_one_sql", new Baseline(7, 7, 7000), 100, new Recurrence(5, 5)),
            LinkLevel.Weak, ["mechanism_compatible"] },
        { "busy hour, usual share", Input("memory_saturation", "n_plus_one_sql", Daily(), 100) with
            { WindowTotal = 10_000 }, LinkLevel.Weak, ["mechanism_compatible"] },
        { "recurring, rare", Input("memory_saturation", "redundant_sql", new Baseline(5, 1, 7000), 100,
            new Recurrence(5, 4)), LinkLevel.Strong, ["mechanism_compatible", "recurrence"] },
        { "new finding", Input("oom_kill", "excessive_fanout", NewFinding), LinkLevel.Strong,
            ["mechanism_compatible", "intensity_new"] },
        { "no ledger", Input("oom_kill", "n_plus_one_sql", Unknown, 1, new Recurrence(3, 3)), LinkLevel.Medium,
            ["mechanism_compatible", "intensity_unknown", "recurrence"] },
        { "saturation boost", Input("oom_kill", "chatty_service", Daily(), 20, saturation: 0.95), LinkLevel.Strong,
            ["mechanism_compatible", "intensity", "saturation"] },
        { "saturation not needed, high intensity", Input("oom_kill", "chatty_service", Daily(100, 1), 100,
            new Recurrence(5, 2), 0.95), LinkLevel.Strong,
            ["mechanism_compatible", "intensity", "recurrence"] },
        { "seen on uncovered days only", Input("oom_kill", "n_plus_one_sql", NewFinding) with { SeenInBaselineDays = true },
            LinkLevel.Weak, ["mechanism_compatible", "intensity_unknown"] },
        { "new by detections, days seen",Input("oom_kill", "chatty_service", new Baseline(7, 3, 0)), LinkLevel.Strong,
            ["mechanism_compatible", "intensity_new"] },
        { "not new, no days seen", Input("oom_kill", "chatty_service", new Baseline(7, 0, 50), 10), LinkLevel.Strong,
            ["mechanism_compatible", "intensity"] },
        { "no boost below 0.9", Input("oom_kill", "chatty_service", Daily(), 20, saturation: 0.85), LinkLevel.Medium,
            ["mechanism_compatible", "intensity"] },
        { "incompatible", Input("oom_kill", "slow_sql", NewFinding, 1, new Recurrence(5, 5), 0.99), LinkLevel.Weak,
            ["mechanism_incompatible", "intensity_new", "recurrence"] },
        { "partial high", Input("restart", "slow_http", NewFinding), LinkLevel.Medium,
            ["mechanism_partial", "intensity_new"] },
        { "partial medium", Input("other", "pool_saturation", Daily(), 20), LinkLevel.Weak,
            ["mechanism_partial", "intensity"] },
        { "unknown kind", Input("brand_new_kind", "n_plus_one_sql", NewFinding), LinkLevel.Medium,
            ["mechanism_partial", "intensity_new"] },
        { "unknown type, memory kind", Input("oom_kill", "brand_new_type", NewFinding), LinkLevel.Weak,
            ["mechanism_incompatible", "intensity_new"] },
        { "unknown type, restart", Input("restart", "brand_new_type", NewFinding), LinkLevel.Medium,
            ["mechanism_partial", "intensity_new"] },
        { "empty window total", Input("oom_kill", "n_plus_one_sql", Daily(), 5) with { WindowTotal = 0 },
            LinkLevel.Weak, ["mechanism_compatible", "intensity_unknown"] },
        { "empty baseline total", Input("oom_kill", "n_plus_one_sql", Daily(), 5) with { BaselineTotal = 0 },
            LinkLevel.Weak, ["mechanism_compatible", "intensity_unknown"] },
        { "only new findings, no known total", Input("oom_kill", "n_plus_one_sql", NewFinding, 5) with
            { WindowTotal = 0, BaselineTotal = 0 }, LinkLevel.Strong, ["mechanism_compatible", "intensity_new"] },
        { "deploy regression", Input("deploy", "n_plus_one_sql", NewFinding, afterDeploy: true), LinkLevel.Strong,
            ["appeared_after_deploy", "intensity_new"] },
        { "deploy other", Input("deploy", "n_plus_one_sql", Daily(), 100, new Recurrence(5, 5)),
            LinkLevel.Weak, ["mechanism_incompatible", "intensity"] }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Assess_gives_the_level_and_reasons(string name, LinkInput input, LinkLevel level, string[] codes)
    {
        var result = IncidentLinkRules.Assess(input);
        Assert.True(level == result.Level, $"{name}: expected {level}, got {result.Level}");
        Assert.Equal(codes, result.Reasons.Select(r => r.Code));
    }

    [Fact]
    public void Intensity_reason_carries_the_ratio()
    {
        var result = IncidentLinkRules.Assess(Input("oom_kill", "n_plus_one_sql", Daily(), 20));
        var ratio = Assert.Single(result.Reasons, r => r.Code == "intensity").Ratio;
        Assert.Equal(2.0, ratio!.Value, 0.1);
    }

    [Fact]
    public void Spike_reason_carries_its_ratio()
    {
        var result = IncidentLinkRules.Assess(Input("oom_kill", "n_plus_one_sql", new Baseline(7, 7, 700), 300));
        Assert.Equal(300.0 / WindowTotal / (700.0 / BaselineTotal), Assert.Single(result.Reasons, r => r.Code == "intensity").Ratio!.Value, 0.1);
    }

    [Fact]
    public void Intensity_ignores_how_busy_the_service_was()
    {
        // The same finding at its usual 1 % share, at night and at a peak ten times busier.
        var quiet = IncidentLinkRules.Assess(Input("oom_kill", "n_plus_one_sql", Daily(), 10) with { WindowTotal = 1000 });
        var busy = IncidentLinkRules.Assess(Input("oom_kill", "n_plus_one_sql", Daily(), 100) with { WindowTotal = 10_000 });
        Assert.Equal(LinkLevel.Weak, quiet.Level);
        Assert.Equal(LinkLevel.Weak, busy.Level);
    }

    [Fact]
    public void Recurrence_reason_carries_k_n_and_the_usual_share()
    {
        var result = IncidentLinkRules.Assess(Input("memory_saturation", "redundant_sql", new Baseline(5, 1, 7000),
            100, new Recurrence(5, 4)));
        var reason = Assert.Single(result.Reasons, r => r.Code == "recurrence");
        Assert.Equal((4, 5, 0.2), (reason.K, reason.N, reason.UsualShare));
        Assert.DoesNotContain(result.Reasons, r => r.Code == "intensity");
    }

    [Fact]
    public void Recurrence_without_ledger_coverage_carries_no_usual_share()
    {
        var result = IncidentLinkRules.Assess(Input("oom_kill", "n_plus_one_sql", Unknown, 1, new Recurrence(3, 3)));
        var reason = Assert.Single(result.Reasons, r => r.Code == "recurrence");
        Assert.Equal((3, 3), (reason.K, reason.N));
        Assert.Null(reason.UsualShare);
        Assert.Equal("""{"code":"recurrence","k":3,"n":3}""", JsonSerializer.Serialize(reason, Json));
    }

    [Fact]
    public void Saturation_reason_carries_the_value()
    {
        var result = IncidentLinkRules.Assess(Input("oom_kill", "chatty_service", Daily(), 20, saturation: 0.95));
        Assert.Equal(0.95, Assert.Single(result.Reasons, r => r.Code == "saturation").Value);
    }

    [Fact]
    public void Reasons_serialize_snake_case_without_nulls_and_levels_as_words()
    {
        var reason = new LinkReason("recurrence", K: 4, N: 5, UsualShare: 0.2);
        Assert.Equal("""{"code":"recurrence","k":4,"n":5,"usual_share":0.2}""",
            JsonSerializer.Serialize(reason, Json));
        Assert.Equal("\"strong\"", JsonSerializer.Serialize(LinkLevel.Strong, Json));
    }
}
