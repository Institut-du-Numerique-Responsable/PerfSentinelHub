using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

public sealed class DetectionVolumeFoldTests
{
    private const long DayMs = 86_400_000;
    private const string Signature = "n_plus_one_sql:order-svc:SELECT * FROM orders WHERE id = ?";

    // Days 10 to 12, judged on day 20 so no window day is the current one.
    private static readonly VolumeWindow Window = new(10, 12);
    private const int Today = 20;

    private static DayCoverage Day(
        int day,
        bool sealedDay = true,
        bool gap = false,
        long overflow = 0,
        long shed = 0,
        long rejected = 0,
        double samplingRate = 1.0,
        string ledger = "L")
    {
        return new DayCoverage(ledger, day, sealedDay, gap, overflow, shed, rejected, samplingRate);
    }

    private static (string, DetectionCountRow) Count(int day, long detections, long? extraIoOps = 10,
        string ledger = "L")
    {
        return (ledger, new DetectionCountRow(day, Signature, detections, extraIoOps));
    }

    private static DetectionVolume Whole(params (string, DetectionCountRow)[] counts)
    {
        return DetectionVolumeFold.ForSource([Day(10), Day(11), Day(12)], counts, Window, Today, false);
    }

    [Fact]
    public void No_covered_day_is_null()
    {
        var volume = DetectionVolumeFold.ForSource([], [Count(11, 4)], Window, Today, false);

        Assert.Null(volume.Detections);
        Assert.Null(volume.Complete);
        Assert.Null(volume.AvoidableIoOps);
        Assert.Null(volume.SamplingRate);
        Assert.Equal(["uncovered_days"], volume.Gaps);
    }

    [Fact]
    public void A_covered_day_without_rows_is_zero()
    {
        var volume = Whole();

        Assert.Equal(0, volume.Detections);
        Assert.Equal(0, volume.AvoidableIoOps);
        Assert.True(volume.Complete);
        Assert.Empty(volume.Gaps);
        Assert.Equal(1.0, volume.SamplingRate);
    }

    [Fact]
    public void A_whole_window_sums_its_counts()
    {
        var volume = Whole(Count(10, 3, 30), Count(12, 4, 40));

        Assert.Equal(7, volume.Detections);
        Assert.Equal(70, volume.AvoidableIoOps);
        Assert.True(volume.Complete);
    }

    [Fact]
    public void A_counted_row_without_io_makes_the_io_unknown()
    {
        var volume = Whole(Count(10, 3, 30), Count(11, 4, null));

        Assert.Equal(7, volume.Detections);
        Assert.Null(volume.AvoidableIoOps);
    }

    [Fact]
    public void A_day_without_coverage_is_an_uncovered_gap_and_the_rest_still_sums()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(12)], [Count(10, 3), Count(11, 100), Count(12, 4)], Window, Today, false);

        // Day 11 has no coverage row, so its count is not trusted either.
        Assert.Equal(7, volume.Detections);
        Assert.False(volume.Complete);
        Assert.Equal(["uncovered_days"], volume.Gaps);
    }

    [Fact]
    public void The_current_day_is_an_open_day()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11), Day(12)], [Count(12, 2)], Window, 12, false);

        Assert.Equal(2, volume.Detections);
        Assert.False(volume.Complete);
        Assert.Equal(["open_day"], volume.Gaps);
    }

    [Fact]
    public void An_unsealed_day_is_an_open_day()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, false), Day(12)], [], Window, Today, false);

        Assert.Equal(["open_day"], volume.Gaps);
    }

    [Fact]
    public void A_gap_is_a_restart()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, gap: true), Day(12)], [], Window, Today, false);

        Assert.Equal(["restart"], volume.Gaps);
    }

    [Fact]
    public void A_sampled_day_is_sampling_and_serves_its_rate()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, samplingRate: 0.5), Day(12)], [Count(11, 6)], Window, Today, false);

        Assert.Equal(6, volume.Detections);
        Assert.Equal(["sampling"], volume.Gaps);
        Assert.Equal(0.5, volume.SamplingRate);
    }

    [Fact]
    public void Shed_traces_are_shed()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, shed: 3), Day(12)], [], Window, Today, false);

        Assert.Equal(["shed"], volume.Gaps);
    }

    [Fact]
    public void Rejected_ingest_is_ingest_rejected()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, rejected: 2), Day(12)], [], Window, Today, false);

        Assert.Equal(["ingest_rejected"], volume.Gaps);
    }

    [Fact]
    public void Overflow_counts_only_on_a_day_the_signature_has_no_row()
    {
        var counted = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, overflow: 9), Day(12)], [Count(11, 5)], Window, Today, false);
        var missed = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, overflow: 9), Day(12)], [Count(10, 5)], Window, Today, false);

        Assert.True(counted.Complete);
        Assert.Empty(counted.Gaps);
        Assert.False(missed.Complete);
        Assert.Equal(["overflow"], missed.Gaps);
    }

    [Fact]
    public void Several_instances_are_multi_instance()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11), Day(12)], [], Window, Today, true);

        Assert.Equal(["multi_instance"], volume.Gaps);
    }

    [Fact]
    public void Reasons_are_sorted_and_counted_once()
    {
        var volume = DetectionVolumeFold.ForSource(
            [Day(10, gap: true, shed: 1), Day(11, false, gap: true, samplingRate: 0.25)],
            [], Window, Today, true);

        Assert.Equal(
            ["multi_instance", "open_day", "restart", "sampling", "shed", "uncovered_days"],
            volume.Gaps);
        Assert.Equal(0.25, volume.SamplingRate);
    }

    [Fact]
    public void Two_ledgers_of_one_source_add_up()
    {
        // A restart on day 11: the first ledger covers its morning, the second the rest.
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11), Day(11, gap: true, ledger: "M"), Day(12, ledger: "M")],
            [Count(10, 2), Count(11, 3), Count(11, 4, ledger: "M"), Count(12, 1, ledger: "M")],
            Window, Today, false);

        Assert.Equal(10, volume.Detections);
        Assert.Equal(["restart"], volume.Gaps);
    }

    [Fact]
    public void An_overflow_is_not_hidden_by_another_ledger_s_row()
    {
        // Two pods on day 11: L overflowed before the signature had a slot, M counted it.
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, overflow: 9), Day(11, ledger: "M"), Day(12)],
            [Count(11, 5, ledger: "M")],
            Window, Today, false);

        Assert.Equal(5, volume.Detections);
        Assert.False(volume.Complete);
        Assert.Equal(["overflow"], volume.Gaps);
    }

    [Fact]
    public void A_count_whose_own_ledger_has_no_row_that_day_is_not_counted()
    {
        // M covers day 11, but L's day 11 never arrived: a pass merged L's counts page and
        // died before the page carrying that day.
        var volume = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, ledger: "M"), Day(12)],
            [Count(11, 5), Count(11, 2, ledger: "M")],
            Window, Today, false);

        Assert.Equal(2, volume.Detections);
        Assert.False(volume.Complete);
        Assert.Equal(["open_day"], volume.Gaps);
    }

    [Fact]
    public void Overlapping_polled_ledgers_are_multi_instance()
    {
        LedgerLife[] ledgers =
        [
            new(10 * DayMs, 12 * DayMs, true),
            new(11 * DayMs, 13 * DayMs, true)
        ];

        Assert.True(DetectionVolumeFold.MultiInstance(ledgers, Window));
    }

    [Fact]
    public void Overlapping_pushed_ledgers_are_not_multi_instance()
    {
        LedgerLife[] ledgers =
        [
            new(10 * DayMs, 12 * DayMs, true),
            new(11 * DayMs, 13 * DayMs, false)
        ];

        Assert.False(DetectionVolumeFold.MultiInstance(ledgers, Window));
    }

    [Fact]
    public void Successive_or_out_of_window_ledgers_are_not_multi_instance()
    {
        LedgerLife[] successive =
        [
            new(10 * DayMs, 11 * DayMs, true),
            new(11 * DayMs + 1, 13 * DayMs, true)
        ];
        LedgerLife[] before =
        [
            new(1 * DayMs, 5 * DayMs, true),
            new(2 * DayMs, 6 * DayMs, true)
        ];

        Assert.False(DetectionVolumeFold.MultiInstance(successive, Window));
        Assert.False(DetectionVolumeFold.MultiInstance(before, Window));
    }

    [Fact]
    public void Finding_level_is_null_only_when_every_source_is()
    {
        var unknown = DetectionVolumeFold.ForSource([], [], Window, Today, false);
        var known = Whole(Count(10, 3, 30));
        var sampled = DetectionVolumeFold.ForSource(
            [Day(10), Day(11, samplingRate: 0.5), Day(12)], [Count(11, 4, null)], Window, Today, false);

        var none = DetectionVolumeFold.ForFinding([unknown, unknown]);
        Assert.Null(none.Detections);
        Assert.Null(none.Complete);
        Assert.Null(none.AvoidableIoOps);
        Assert.Null(none.SamplingRate);
        Assert.Equal(["uncovered_days"], none.Gaps);

        var partial = DetectionVolumeFold.ForFinding([unknown, known, sampled]);
        Assert.Equal(7, partial.Detections);
        Assert.Equal(30, partial.AvoidableIoOps);
        Assert.False(partial.Complete);
        Assert.Equal(0.5, partial.SamplingRate);
        Assert.Equal(["sampling", "uncovered_days"], partial.Gaps);

        var whole = DetectionVolumeFold.ForFinding([known, Whole(Count(12, 1, 5))]);
        Assert.Equal(4, whole.Detections);
        Assert.Equal(35, whole.AvoidableIoOps);
        Assert.True(whole.Complete);
        Assert.Empty(whole.Gaps);
    }
}
