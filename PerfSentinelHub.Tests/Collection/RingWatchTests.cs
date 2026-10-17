using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests.Collection;

public sealed class RingWatchTests
{
    private const long Start = 1_760_000_000_000;

    [Fact]
    public void A_first_reading_has_nothing_to_compare_with()
    {
        var rings = new RingWatch();

        Assert.Null(rings.Measure("prod", Status(60, 100, 100), At(60)));
    }

    [Fact]
    public void The_pushes_beyond_the_ring_left_it_before_the_next_poll()
    {
        // 300 findings went in between the two polls, the ring holds 100 of
        // them, so 200 were evicted before the Hub could read them.
        var rings = new RingWatch();
        rings.Measure("prod", Status(60, 100, 100), At(60));

        Assert.Equal(200, rings.Measure("prod", Status(120, 400, 100), At(120)));
        Assert.Equal(0, rings.Missed("prod"));
    }

    [Fact]
    public void A_ring_that_holds_every_new_finding_missed_none()
    {
        var rings = new RingWatch();
        rings.Measure("prod", Status(60, 100, 100), At(60));

        Assert.Equal(0, rings.Measure("prod", Status(120, 150, 100), At(120)));
    }

    [Fact]
    public void A_restart_starts_a_new_baseline_instead_of_a_negative_count()
    {
        var rings = new RingWatch();
        rings.Measure("prod", Status(600, 50_000, 100), At(600));

        Assert.Null(rings.Measure("prod", Status(30, 120, 100), At(660)));
        Assert.Equal(100, rings.Measure("prod", Status(90, 320, 100), At(720)));
    }

    [Fact]
    public void A_restart_whose_uptime_climbed_past_the_last_reading_is_still_a_restart()
    {
        // Read at uptime 10 s, restarted right after, read again 60 s later at
        // uptime 50 s with more in the counter: both figures rose, only the
        // estimated start moved.
        var rings = new RingWatch();
        rings.Measure("prod", Status(10, 100, 100), At(10));

        Assert.Null(rings.Measure("prod", Status(50, 400, 100), At(70)));
    }

    [Fact]
    public void A_restart_clears_the_hint_but_not_the_counter()
    {
        var rings = new RingWatch();
        rings.Measure("prod", Status(60, 100, 100), At(60));
        rings.Record("prod", rings.Measure("prod", Status(120, 400, 100), At(120))!.Value);
        Assert.Equal(200, rings.MissedSinceStart("prod"));

        rings.Measure("prod", Status(5, 10, 10), At(3600));

        Assert.Equal(0, rings.MissedSinceStart("prod"));
        Assert.Equal(200, rings.Missed("prod"));
    }

    [Fact]
    public void A_daemon_without_the_counter_cannot_be_judged()
    {
        var rings = new RingWatch();
        var old = new DaemonStatus("0.31.1", 60, null, null, null, null, 100, 100);

        Assert.Null(rings.Measure("prod", old, At(60)));
        Assert.Null(rings.Measure("prod", old with { UptimeSeconds = 120 }, At(120)));
    }

    [Fact]
    public void The_recorded_misses_add_up_per_source()
    {
        var rings = new RingWatch();
        rings.Record("prod", 200);
        rings.Record("prod", 100);
        rings.Record("prod", 0);

        Assert.Equal(300, rings.Missed("prod"));
        Assert.Equal(300, rings.MissedSinceStart("prod"));
        Assert.Equal(0, rings.Missed("staging"));
        Assert.Contains("max_retained_findings", RingWatch.Hint(300).Message, StringComparison.Ordinal);
        Assert.Contains("since the daemon last started", RingWatch.Hint(300).Message, StringComparison.Ordinal);
        Assert.Equal("tuning", RingWatch.Hint(300).Kind);
    }

    // The Hub's clock when the daemon has been up `uptime` seconds since Start.
    private static long At(long uptime)
    {
        return Start + uptime * 1000;
    }

    private static DaemonStatus Status(long uptime, long total, long stored)
    {
        return new DaemonStatus("0.31.2", uptime, null, null, null, null, stored, 100)
        {
            FindingsStoredTotal = total
        };
    }
}
