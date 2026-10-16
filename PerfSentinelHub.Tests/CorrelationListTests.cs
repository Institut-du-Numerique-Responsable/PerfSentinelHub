using Microsoft.Extensions.Options;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class CorrelationListTests : IAsyncLifetime
{
    private const long Now = 1_800_010_000_000;

    // Running, closed by the daemon, and silent past its gap plus 15 minutes.
    private static readonly string Running = EpisodeJson.Id(1);
    private static readonly string Closed = EpisodeJson.Id(2);
    private static readonly string Silent = EpisodeJson.Id(3);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-episodes-list-{Guid.NewGuid():N}.db");

    private HubDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        _database = new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), TimeProvider.System);
        await _database.InitializeAsync(cancellationToken);
        await _database.TryUpsertEpisodesAsync("daemon-a", EpisodeJson.Parsed(
                EpisodeJson.Episode(Running, Now - 600_000, Now - 60_000, null, EpisodeJson.Id(0xa1)),
                EpisodeJson.Episode(Closed, Now - 7_200_000, Now - 6_600_000, Now - 6_600_000, EpisodeJson.Id(0xa2),
                    "gateway-svc", "order-svc"),
                EpisodeJson.Episode(Silent, Now - 5_000_000, Now - 3_000_000, null, EpisodeJson.Id(0xa3),
                    "cart-svc", "stock-svc")),
            Now, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    private static CorrelationQuery All()
    {
        return new CorrelationQuery(null, null, null, null, null, false, Now, 0, 200);
    }

    private Task<IReadOnlyList<StoredEpisode>> ListAsync(CorrelationQuery query)
    {
        return _database.ListEpisodesAsync(query, TestContext.Current.CancellationToken);
    }

    private static string Ids(IEnumerable<StoredEpisode> rows)
    {
        return string.Join(',', rows.Select(row => row.Id[^1..]));
    }

    [Fact]
    public async Task Episodes_come_newest_first_with_a_silent_one_served_as_ended()
    {
        var rows = await ListAsync(All());

        Assert.Equal("1,3,2", Ids(rows));
        Assert.Null(rows[0].EndedAtMs);
        Assert.Null(rows[0].EndedBy);
        Assert.Equal(Now - 3_000_000, rows[1].EndedAtMs);
        Assert.Equal("stale", rows[1].EndedBy);
        Assert.Equal(Now - 6_600_000, rows[2].EndedAtMs);
        Assert.Null(rows[2].EndedBy);
        Assert.Equal("order-svc", rows[0].Source.Service);
        Assert.Equal("pool_saturation", rows[0].Target.FindingType);
        Assert.Equal(Now, rows[0].FirstSeenMs);
    }

    [Fact]
    public async Task Active_keeps_the_running_episodes_and_not_a_silent_one()
    {
        Assert.Equal("1", Ids(await ListAsync(All() with { Active = true })));
    }

    [Fact]
    public async Task A_late_close_still_lands_on_an_episode_served_as_stale()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var connection = await _database.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            // The stale rule is a read-time verdict: the row itself holds no end.
            command.CommandText = "SELECT ended_at_ms IS NULL FROM correlation_episodes WHERE id = $id;";
            command.Parameters.AddWithValue("$id", Silent);
            Assert.Equal(1L, await command.ExecuteScalarAsync(cancellationToken));
        }

        await _database.TryUpsertEpisodesAsync("daemon-a", EpisodeJson.Parsed(
                EpisodeJson.Episode(Silent, Now - 5_000_000, Now - 3_000_000, Now - 3_000_000, EpisodeJson.Id(0xa3),
                    "cart-svc", "stock-svc")),
            Now + 1, cancellationToken);

        var row = Assert.Single(await ListAsync(All() with { PairKey = EpisodeJson.Id(0xa3) }));
        Assert.Null(row.EndedBy);
        Assert.Equal(Now - 3_000_000, row.EndedAtMs);
    }

    [Fact]
    public async Task A_service_matches_either_side()
    {
        Assert.Equal("1,2", Ids(await ListAsync(All() with { Service = "order-svc" })));
    }

    [Theory]
    [InlineData(-6_600_000L, -6_000_000L, "2")]
    [InlineData(-100_000L, 0L, "1")]
    [InlineData(-3_000_000L, -2_000_000L, "3")]
    [InlineData(-600_000L, -600_000L, "1")]
    [InlineData(-6_500_000L, -5_100_000L, "")]
    public async Task From_and_to_keep_the_episodes_overlapping_the_span(long from, long to, string expected)
    {
        // In order: an end exactly at from, a running episode, a served end exactly at from,
        // a start exactly at to, and a span between two episodes that touches neither.
        Assert.Equal(expected, Ids(await ListAsync(All() with { FromMs = Now + from, ToMs = Now + to })));
    }

    [Fact]
    public async Task Pages_and_the_closed_filters_narrow_the_listing()
    {
        Assert.Equal("3", Ids(await ListAsync(All() with { Offset = 1, Limit = 1 })));
        Assert.Equal("2", Ids(await ListAsync(All() with { PairKey = EpisodeJson.Id(0xa2) })));
        Assert.Equal("", Ids(await ListAsync(All() with { SourceIds = ["daemon-b"] })));
        Assert.Equal("1,3,2", Ids(await ListAsync(All() with { SourceIds = ["daemon-a"] })));
    }

    // The listing orders on these three columns, so the index serves it without a sort.
    [Fact]
    public async Task The_listing_index_covers_its_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(name, ',') FROM pragma_index_info('ix_episodes_listing');";
        Assert.Equal("started_at_ms,id,source_id", (string?)await command.ExecuteScalarAsync(cancellationToken));
    }
}
