using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

// Each test counts at a source of its own, so the other tests' done actions stay off its watch list.
public sealed class CardStorageTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>
{
    private const int Today = 20741;
    private const long DoneMs = Today * 86_400_000L + 36_000_000;
    private const string LedgerA = "00000000-0000-4000-8000-00000000000a";
    private const string LedgerB = "00000000-0000-4000-8000-00000000000b";

    private HubDatabase Database => factory.Database;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // One pushed page of one ledger: a single count on a single day.
    private Task CountAsync(
        string sourceId, string ledgerId, int day, string signature, long detections, long? passes = null,
        bool sealedDay = false)
    {
        var page = new DetectionLedgerPage(
            ledgerId, 0, day, day, null,
            [new DetectionCountRow(day, signature, detections, null, passes)],
            [new DetectionDayRow(day, sealedDay, false, 0, null, 0, 0, 1.0)],
            null);
        return Database.MergeDetectionPageAsync(sourceId, page, true, DoneMs, Token);
    }

    private Task DoneAsync(string signature, long atMs)
    {
        return Database.CreateActionAsync(signature, ActionStatuses.Done, null, null, "alice", atMs, null, Token);
    }

    [Fact]
    public async Task The_watch_list_is_capped_newest_first()
    {
        string[] signatures = ["slow_sql:capped:a", "slow_http:capped:b", "excessive_fanout:capped:c"];
        for (var index = 0; index < signatures.Length; index++)
        {
            await CountAsync("capped", LedgerA, Today - 1, signatures[index], 1);
            await DoneAsync(signatures[index], DoneMs + index + 1);
        }

        Assert.Equal(["excessive_fanout:capped:c", "slow_http:capped:b"],
            await Database.ReadWatchAsync("capped", 2, Token));
    }

    [Fact]
    public async Task A_day_with_a_detection_in_another_ledger_does_not_confirm()
    {
        // The daemon restarted that day: the new ledger saw only passes below the threshold, the old one a detection.
        const string signature = "slow_sql:restarted:a";
        await CountAsync("restarted", LedgerA, Today - 1, signature, 2);
        await DoneAsync(signature, DoneMs);
        await CountAsync("restarted", LedgerA, Today + 1, signature, 0, 5, true);
        await CountAsync("restarted", LedgerB, Today + 1, signature, 1, null, true);

        Assert.Equal([signature], await Database.ReadWatchAsync("restarted", 10, Token));
    }
}
