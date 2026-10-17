using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class RingWatchTests
{
    [Fact]
    public void A_first_reading_has_nothing_to_compare_with()
    {
        var rings = new RingWatch();

        Assert.Null(rings.Observe("prod", Status(60, 100, 100), false));
        Assert.Equal(0, rings.Missed("prod"));
    }

    [Fact]
    public void The_pushes_beyond_the_ring_left_it_before_the_next_poll()
    {
        // 300 findings went in between the two polls, the ring holds 100 of
        // them, so 200 were evicted before the Hub could read them.
        var rings = new RingWatch();
        rings.Observe("prod", Status(60, 100, 100), false);

        Assert.Equal(200, rings.Observe("prod", Status(120, 400, 100), false));
        Assert.Equal(200, rings.Missed("prod"));
    }

    [Fact]
    public void A_ring_that_holds_every_new_finding_missed_none()
    {
        var rings = new RingWatch();
        rings.Observe("prod", Status(60, 100, 100), false);

        Assert.Equal(0, rings.Observe("prod", Status(120, 150, 100), false));
        Assert.Equal(0, rings.Missed("prod"));
    }

    [Fact]
    public void A_restart_starts_a_new_baseline_instead_of_a_negative_count()
    {
        var rings = new RingWatch();
        rings.Observe("prod", Status(600, 50_000, 100), false);

        Assert.Null(rings.Observe("prod", Status(30, 120, 100), false));
        Assert.Equal(100, rings.Observe("prod", Status(90, 320, 100), false));
    }

    [Fact]
    public void A_daemon_that_pushes_to_the_hub_loses_nothing_to_the_poll()
    {
        // What the poll did not read reached the Hub by the push, but the
        // baseline still moves, so a later read without the push does not
        // count the pushed stretch as missed.
        var rings = new RingWatch();
        rings.Observe("prod", Status(60, 100, 100), true);

        Assert.Equal(0, rings.Observe("prod", Status(120, 400, 100), true));
        Assert.Equal(0, rings.Observe("prod", Status(180, 450, 100), false));
        Assert.Equal(0, rings.Missed("prod"));
    }

    [Fact]
    public void A_daemon_without_the_counter_cannot_be_judged()
    {
        var rings = new RingWatch();
        var old = new DaemonStatus("0.31.1", 60, null, null, null, null, 100, 100);

        Assert.Null(rings.Observe("prod", old, false));
        Assert.Null(rings.Observe("prod", old with { UptimeSeconds = 120 }, false));
    }

    [Fact]
    public void The_misses_add_up_per_source()
    {
        var rings = new RingWatch();
        rings.Observe("prod", Status(60, 100, 100), false);
        rings.Observe("prod", Status(120, 400, 100), false);
        rings.Observe("prod", Status(180, 600, 100), false);

        Assert.Equal(300, rings.Missed("prod"));
        Assert.Equal(0, rings.Missed("staging"));
        Assert.Contains("max_retained_findings", RingWatch.Hint(300).Message, StringComparison.Ordinal);
        Assert.Equal("tuning", RingWatch.Hint(300).Kind);
    }

    private static DaemonStatus Status(long uptime, long total, long stored)
    {
        return new DaemonStatus("0.31.2", uptime, null, null, null, null, stored, 100)
        {
            FindingsStoredTotal = total
        };
    }
}
