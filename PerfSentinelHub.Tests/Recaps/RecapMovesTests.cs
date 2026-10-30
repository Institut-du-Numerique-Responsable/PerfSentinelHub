using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapMovesTests
{
    private const double Threshold = 0.2;

    private static (string Trend, double? Change) Move(long? current, long? previous, bool io = true)
    {
        var now = io ? RecapFindings.Of("s", io: current) : RecapFindings.Of("s", detections: current);
        var before = io ? RecapFindings.Of("s", io: previous) : RecapFindings.Of("s", detections: previous);
        return RecapMoves.Trend(now, before, Threshold);
    }

    private static RecapPriority Priority(int rank, string trend)
    {
        return new RecapPriority(
            rank, "n_plus_one_sql", "warning", "svc", "plain", null, null, null, false, true, trend, null, null,
            null, null, null, null, null, null, null, null, null);
    }

    private static RecapMove MoveRow()
    {
        return new RecapMove("n_plus_one_sql", "svc", "plain", "up", null, null, null, null, null);
    }

    private static RecapMovements Moves(
        IReadOnlyList<RecapMove>? up = null,
        IReadOnlyList<RecapMove>? doneStillDetected = null)
    {
        return new RecapMovements([], [], 0, up ?? [], [], [], doneStillDetected ?? []);
    }

    [Fact]
    public void No_previous_is_new()
    {
        Assert.Equal(("new", null), RecapMoves.Trend(RecapFindings.Of("s", io: 5), null, Threshold));
    }

    [Fact]
    public void Twenty_five_percent_up_is_up_at_a_fifth()
    {
        Assert.Equal(("up", 0.25), Move(125, 100));
    }

    [Fact]
    public void Fifteen_percent_down_is_flat()
    {
        Assert.Equal("flat", Move(85, 100).Trend);
    }

    [Fact]
    public void Twenty_percent_down_is_down()
    {
        Assert.Equal(("down", -0.2), Move(80, 100));
    }

    [Fact]
    public void Io_wins_over_detections_when_either_side_has_it()
    {
        var now = RecapFindings.Of("s", io: 200, detections: 10);
        var before = RecapFindings.Of("s", io: 100, detections: 100);

        Assert.Equal(("up", 1.0), RecapMoves.Trend(now, before, Threshold));
    }

    [Fact]
    public void Detections_are_the_metric_without_io()
    {
        Assert.Equal(("down", -0.5), Move(5, 10, false));
    }

    [Fact]
    public void No_metric_is_unknown()
    {
        Assert.Equal(("unknown", null), Move(null, null));
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData(0, null)]
    public void A_side_the_data_cannot_compare_is_unknown(int? current, int? previous)
    {
        Assert.Equal(("unknown", null), Move(current, previous, false));
    }

    [Fact]
    public void Appearing_from_nothing_is_up_without_a_change()
    {
        Assert.Equal(("up", null), Move(50, 0));
        Assert.Equal(("flat", 0), Move(0, 0, false));
    }

    [Fact]
    public void NothingMoved_ignores_a_move_below_rank_ten()
    {
        var ranked = Enumerable.Range(1, 10).Select(rank => Priority(rank, "flat"))
            .Append(Priority(11, "up")).ToList();

        Assert.True(RecapMoves.NothingMoved(Moves([MoveRow()]), ranked));
        Assert.False(RecapMoves.NothingMoved(Moves(), [Priority(1, "down")]));
    }

    [Fact]
    public void NothingMoved_is_false_with_one_contradicted_action()
    {
        Assert.False(RecapMoves.NothingMoved(Moves(doneStillDetected: [MoveRow()]), [Priority(1, "flat")]));
    }
}
