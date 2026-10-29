using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

internal static class RecapFindings
{
    public static RecapFinding Of(
        string signature,
        string severity = "warning",
        long? io = null,
        long? detections = null,
        string type = "n_plus_one_sql")
    {
        return new RecapFinding(
            signature, type, severity, "svc", "GET /x", null, null, null, null, "daemon_production",
            detections, io, detections is not null, ["s"]);
    }
}

public sealed class RecapRankingTests
{
    [Fact]
    public void Io_first_by_volume_then_severity()
    {
        var order = RecapRanking.Order(
        [
            RecapFindings.Of("e", "info", 0, 9),
            RecapFindings.Of("d", "critical"),
            RecapFindings.Of("c", "critical", 0, 5),
            RecapFindings.Of("b", "critical", 100, 1),
            RecapFindings.Of("a", "warning", 400, 1)
        ]);

        Assert.Equal(["a", "b", "c", "d", "e"], order.Select(f => f.Signature));
    }

    [Fact]
    public void Ties_break_on_detections_then_signature()
    {
        var order = RecapRanking.Order(
        [
            RecapFindings.Of("z", io: 100, detections: 3),
            RecapFindings.Of("b", io: 100, detections: 7),
            RecapFindings.Of("a", io: 100, detections: 7)
        ]);

        Assert.Equal(["a", "b", "z"], order.Select(f => f.Signature));
    }

    [Fact]
    public void Null_detections_rank_last_in_a_tie()
    {
        var order = RecapRanking.Order(
        [
            RecapFindings.Of("a", "critical"),
            RecapFindings.Of("b", "critical", detections: 0)
        ]);

        Assert.Equal(["b", "a"], order.Select(f => f.Signature));
    }
}
