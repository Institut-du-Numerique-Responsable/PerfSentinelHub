using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Support;

/// <summary>
///     A share card's store on Wednesday 2026-11-04 10:00 UTC: pushed ledger pages (so a sealed day
///     is sealed), findings and the done actions that freeze them. Sources a and b are prod daemons,
///     prod-eu and prod-us.
/// </summary>
internal sealed class CardSeed(HubDatabase database)
{
    public const long DayMs = 86_400_000;
    public const int Today = 20761;
    public const string Now = "2026-11-04T10:00:00Z";
    public const string Ledger = "00000000-0000-4000-8000-0000000000c1";

    public static readonly RecapPeriod July = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 7, 1));
    public static readonly RecapPeriod August = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 8, 1));
    public static readonly RecapPeriod September = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 9, 1));
    public static readonly RecapPeriod October = RecapPeriod.Of(RecapPeriodKind.Month, new DateOnly(2026, 10, 1));

    public static readonly SourceOptions[] Sources =
    [
        new() { Id = "a", Name = "prod-eu", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:1") },
        new() { Id = "b", Name = "prod-us", Environment = "prod", BaseUrl = new Uri("http://127.0.0.1:2") }
    ];

    public static long NowMs => Today * DayMs + 36_000_000;

    /// <summary>Every day of the period sealed and whole; change rewrites a day to make it otherwise.</summary>
    public static DetectionDayRow[] Sealed(RecapPeriod period, Func<DetectionDayRow, DetectionDayRow?>? change = null)
    {
        return
        [
            .. Enumerable.Range(period.FromDay, period.ToDay - period.FromDay + 1)
                .Select(day => new DetectionDayRow(day, true, false, 0, null, 0, 0, 1.0))
                .Select(day => change is null ? day : change(day))
                .OfType<DetectionDayRow>()
        ];
    }

    public Task LedgerAsync(string source, IReadOnlyList<DetectionDayRow> days, params DetectionCountRow[] counts)
    {
        var page = new DetectionLedgerPage(
            Ledger, (July.FromDay - 1) * DayMs, Today, July.FromDay - 1, null, counts, days, null);
        return database.MergeDetectionPageAsync(source, page, true, NowMs, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A finding seen and counted at the source on the done day, then done at 10:00 that day, so
    ///     its envelope is frozen. The figures a story cites sit in its pattern.
    /// </summary>
    public async Task FixAsync(
        string source,
        string signature,
        int doneDay,
        string type = "n_plus_one_sql",
        string service = "order-svc",
        string endpoint = "GET /orders",
        long? occurrences = 41,
        long? p99Us = null,
        string? firstTimestamp = "2026-10-12T08:30:00Z")
    {
        var envelope = FindingJson.Envelope(signature, type, service: service, endpoint: endpoint);
        var finding = envelope["finding"]!;
        if (occurrences is { } count)
            finding["pattern"]!["occurrences"] = count;
        if (p99Us is { } p99)
            finding["pattern"]!["span_duration_us_p99"] = p99;
        if (firstTimestamp is not null)
            finding["first_timestamp"] = firstTimestamp;
        var token = TestContext.Current.CancellationToken;
        await database.UpsertBatchAsync(
            new SourceSnapshot(source, source, "prod", "0.38.0"), FindingJson.Batch(envelope),
            doneDay * DayMs + 3_600_000, token);
        await LedgerAsync(source, [], new DetectionCountRow(doneDay, signature, 3, null));
        await DoneAsync(signature, doneDay);
    }

    public Task DoneAsync(string signature, int doneDay)
    {
        return database.CreateActionAsync(signature, ActionStatuses.Done, null, null, "alice",
            doneDay * DayMs + 36_000_000, null, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A fix done before the Hub froze findings: counted at the source and done, but with no
    ///     envelope kept, as an action done before schema V20 reads.
    /// </summary>
    public async Task UnkeptAsync(string source, string signature, int doneDay)
    {
        var token = TestContext.Current.CancellationToken;
        await LedgerAsync(source, [], new DetectionCountRow(doneDay, signature, 3, null));
        await DoneAsync(signature, doneDay);
        await using var connection = await database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE finding_actions SET frozen_finding_json = NULL WHERE signature = $signature;";
        command.Parameters.AddWithValue("$signature", signature);
        await command.ExecuteNonQueryAsync(token);
    }
}
