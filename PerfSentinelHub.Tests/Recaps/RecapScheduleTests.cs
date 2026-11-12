using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

/// <summary>
///     When a closed period is due. The week is 2026-10-19..2026-10-25: its last day is 20751 and it
///     ends at 2026-10-26T00:00Z.
/// </summary>
public sealed class RecapScheduleTests
{
    private const int LastDay = 20751;
    private const long DayMs = 86_400_000;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);
    private static readonly RecapPeriod Week = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2026, 10, 22));
    private static readonly long End = (LastDay + 1L) * DayMs;

    private static readonly SourceOptions[] Sources =
    [
        new() { Id = "a", Name = "Shop", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:1") },
        new() { Id = "b", Name = "Lab", Environment = "lab", BaseUrl = new Uri("http://127.0.0.1:2") },
        new() { Id = "t", Name = "Traces", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:3") }
    ];

    private static SourceCoverage Ledger(string source, bool sealedLastDay, long lastReadMs = long.MaxValue)
    {
        return new SourceCoverage(
            source,
            [new DayCoverage("l", LastDay, sealedLastDay, false, 0, 0, 0, 1.0)],
            [new LedgerLife(0, lastReadMs, true)]);
    }

    [Fact]
    public void Nothing_is_due_before_the_period_ends()
    {
        var coverage = new[] { Ledger("a", true), Ledger("b", true) };

        Assert.False(RecapSchedule.IsDue(Week, coverage, Week.FromMs, MaxDelay));
        Assert.False(RecapSchedule.IsDue(Week, coverage, End - 1, MaxDelay));
        Assert.False(RecapSchedule.IsDue(Week, [], End - 1, MaxDelay));
    }

    [Fact]
    public void Due_once_every_live_ledger_sealed_the_last_day()
    {
        Assert.True(RecapSchedule.IsDue(Week, [Ledger("a", true), Ledger("b", true)], End, MaxDelay));
    }

    [Fact]
    public void One_unsealed_ledger_holds_it_until_the_max_delay()
    {
        var coverage = new[] { Ledger("a", true), Ledger("b", false) };

        Assert.False(RecapSchedule.IsDue(Week, coverage, End, MaxDelay));
        Assert.False(RecapSchedule.IsDue(Week, coverage, End + (long)MaxDelay.TotalMilliseconds - 1, MaxDelay));
        Assert.True(RecapSchedule.IsDue(Week, coverage, End + (long)MaxDelay.TotalMilliseconds, MaxDelay));
    }

    [Fact]
    public void A_ledger_sealed_on_another_day_does_not_count()
    {
        var elsewhere = new SourceCoverage(
            "b", [new DayCoverage("l", LastDay - 1, true, false, 0, 0, 0, 1.0)], [new LedgerLife(0, End, true)]);

        Assert.False(RecapSchedule.IsDue(Week, [Ledger("a", true), elsewhere], End, MaxDelay));
    }

    [Fact]
    public void A_retired_ledger_does_not_hold_the_report_back()
    {
        var retired = Ledger("b", false, Week.FromMs - 1);

        Assert.True(RecapSchedule.IsDue(Week, [Ledger("a", true), retired], End, MaxDelay));
    }

    [Fact]
    public void A_source_that_started_after_the_period_does_not_hold_it_back()
    {
        var late = new SourceCoverage("b", [], [new LedgerLife(End, long.MaxValue, true)]);

        Assert.True(RecapSchedule.IsDue(Week, [Ledger("a", true), late], End, MaxDelay));
    }

    [Fact]
    public void No_ledger_at_all_is_due_at_the_end()
    {
        Assert.True(RecapSchedule.IsDue(Week, [], End, MaxDelay));
    }

    [Fact]
    public void Scope_follows_the_environment_and_source_filters()
    {
        Assert.True(RecapSchedule.TryScope(null, null, Sources, out var all));
        Assert.Null(all);

        Assert.True(RecapSchedule.TryScope("prod", null, Sources, out var prod));
        Assert.Equal(["a", "t"], prod);

        Assert.True(RecapSchedule.TryScope(null, "b", Sources, out var lab));
        Assert.Equal(["b"], lab);

        // Both filters apply: a source outside the environment leaves the empty set.
        Assert.True(RecapSchedule.TryScope("prod", "b", Sources, out var none));
        Assert.Empty(none!);

        Assert.False(RecapSchedule.TryScope("nowhere", null, Sources, out _));
        Assert.False(RecapSchedule.TryScope(null, "zz", Sources, out _));
    }

    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private static long Utc(int month, int day, int hour, int minute = 0)
    {
        return new DateTimeOffset(2026, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    [Fact]
    public void A_report_frozen_in_the_night_waits_for_the_morning_hour_of_its_zone()
    {
        // Monday 2026-11-09 00:30Z is 01:30 in Paris (UTC+1): 09:00 Paris is 08:00Z the same day.
        Assert.Equal(Utc(11, 9, 8), RecapSchedule.DeliveryDueMs(Utc(11, 9, 0, 30), new TimeOnly(9, 0), Paris));
        // In summer Paris is UTC+2: 09:00 Paris is 07:00Z.
        Assert.Equal(Utc(6, 1, 7), RecapSchedule.DeliveryDueMs(Utc(6, 1, 0, 30), new TimeOnly(9, 0), Paris));
    }

    [Fact]
    public void A_report_frozen_past_the_hour_waits_for_the_next_day_and_one_on_it_leaves_at_once()
    {
        Assert.Equal(Utc(11, 10, 8), RecapSchedule.DeliveryDueMs(Utc(11, 9, 8, 1), new TimeOnly(9, 0), Paris));
        Assert.Equal(Utc(11, 9, 8), RecapSchedule.DeliveryDueMs(Utc(11, 9, 8), new TimeOnly(9, 0), Paris));
    }

    [Fact]
    public void A_time_a_clock_change_skips_moves_an_hour_on()
    {
        // 2026-03-29 02:30 does not exist in Paris: clocks jump from 02:00 to 03:00 (01:00Z).
        Assert.Equal(Utc(3, 29, 1, 30), RecapSchedule.DeliveryDueMs(Utc(3, 28, 23), new TimeOnly(2, 30), Paris));
    }

    [Fact]
    public void Without_deliver_at_a_report_is_due_when_generated()
    {
        var subscription = new RecapSubscriptionOptions { Name = "w", Cadences = ["week"], Views = ["pilotage"] };

        Assert.Equal(Utc(11, 9, 0, 30), RecapSchedule.DeliveryDueMs(Utc(11, 9, 0, 30), subscription));
        Assert.Equal(Utc(11, 9, 8), RecapSchedule.DeliveryDueMs(
            Utc(11, 9, 0, 30), subscription with { DeliverAt = "09:00", TimeZone = "Europe/Paris" }));
        // UTC when no zone is named.
        Assert.Equal(Utc(11, 9, 9), RecapSchedule.DeliveryDueMs(
            Utc(11, 9, 0, 30), subscription with { DeliverAt = "09:00" }));
    }
}
