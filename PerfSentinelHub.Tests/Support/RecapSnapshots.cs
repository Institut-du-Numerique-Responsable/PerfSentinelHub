using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Support;

/// <summary>
///     A tech recap snapshot whose engineering values are all distinctive, so a test can look for them
///     in a management rendering. Every row carrying the distinctive endpoint also carries a label, as
///     the management projection keeps an endpoint only where no label names it.
/// </summary>
public static class RecapSnapshots
{
    public const string Signature = "sig-TECH-1";
    public const string Endpoint = "GET /orders/TECH";
    public const string Template = "SELECT tech_col FROM t";
    public const string File = "src/TechOnly.java";
    public const string Suggestion = "TECH-SUGGESTION";
    public const string Fix = "TECH-FIX";
    public const string Confidence = "daemon_production";

    public static readonly string[] Values = [Signature, Endpoint, Template, File, Suggestion, Fix, Confidence];

    /// <summary>2026-10-29 14:32:05 UTC.</summary>
    public const long GeneratedAtMs = 1793284325000;

    public static RecapPriority Priority(int rank, string? label = "Label OK", string? template = Template)
    {
        return new RecapPriority(
            rank, "n_plus_one_sql", "warning", "checkout-api", RecapSentences.For("n_plus_one_sql"), 12, 4000 - rank,
            300, false, true, "up", 0.35, label,
            new RecapAction("done", "alice.martin@acme.com", null, GeneratedAtMs - 86400000, GeneratedAtMs - 3600000,
                "confirmed",
                [new RecapActionEvent("todo", "done", "alice.martin@acme.com", null, "alice", GeneratedAtMs - 3600000)]),
            $"sig-TECH-{rank}", Endpoint, template, $"{Suggestion} with `batch_size` set", new RecapFix($"{Fix} use a join", "ef", "https://example.test/ref"),
            new RecapCodeLocation("TechOnly.load", File, 42, null), Confidence);
    }

    public static RecapMove Move(string? label = "Label OK")
    {
        return new RecapMove("n_plus_one_sql", "checkout-api", RecapSentences.For("n_plus_one_sql"), "up", 0.2, label,
            "sig-TECH-move", Endpoint);
    }

    public static RecapSnapshot Distinctive(int priorities = 1)
    {
        var from = new RecapPeriodInfo("week", "2026-10-19", "2026-10-25", false, [], [], 1.0, null);
        var period = new RecapPeriodInfo("week", "2026-10-26", "2026-11-01", false, [], [], 1.0, null);
        return new RecapSnapshot(
            1, "tech", GeneratedAtMs, period, from, new RecapFilters(null, null, null, null), true,
            new RecapTiles(168900, 150000, 2000, 1000, 0, null, priorities, 0, 2,
                new RecapActionCounts(1, 1, 1, 1, 0, 0)),
            [.. Enumerable.Range(1, priorities).Select(rank => Priority(rank))], 0,
            new RecapMovements([Move()], [Move()], 1, [Move()], [Move()], [Move()], [Move()]), false, ["svc-quiet"],
            false);
    }
}
