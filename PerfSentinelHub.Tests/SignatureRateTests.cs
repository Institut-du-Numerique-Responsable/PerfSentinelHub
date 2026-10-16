using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class SignatureRateTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UnixEpoch.AddDays(30));
    private readonly HubDatabase _database;

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"perf-sentinel-hub-rate-{Guid.NewGuid():N}.db");

    private readonly ListLogger<SignatureRate> _logger = new();
    private readonly HubOptions _options;

    public SignatureRateTests()
    {
        _options = new HubOptions
        {
            DatabasePath = _databasePath,
            NewSignaturesPerHourWarning = 3,
            Sources = [Daemon("noisy"), Daemon("quiet"), Daemon("fresh")]
        };
        _database = new HubDatabase(Options.Create(_options), _clock);
    }

    public void Dispose()
    {
        TestPool.ClearFor(_databasePath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_databasePath + suffix);
    }

    [Fact]
    public async Task A_source_creating_signatures_past_the_threshold_is_flagged_and_logged_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var now = _clock.GetUtcNow();
        // Known for a day, then a burst of new signatures inside the last hour.
        await StoreAsync("noisy", now.AddDays(-1), "old");
        await StoreAsync("noisy", now.AddMinutes(-30), "a", "b", "c", "d");
        await StoreAsync("quiet", now.AddDays(-1), "old");
        // At the threshold, not past it.
        await StoreAsync("quiet", now.AddMinutes(-30), "a", "b", "c");
        var rate = new SignatureRate(_database, Options.Create(_options), _clock, _logger);

        await rate.EvaluateAsync(cancellationToken);
        await rate.EvaluateAsync(cancellationToken);

        Assert.Equal(4, rate.NewSignaturesLastHour("noisy"));
        Assert.True(rate.IsAbnormal("noisy"));
        Assert.Equal(3, rate.NewSignaturesLastHour("quiet"));
        Assert.False(rate.IsAbnormal("quiet"));
        Assert.Single(_logger.Messages, message => message.Contains("noisy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_source_whose_findings_are_all_this_hour_is_not_judged_yet()
    {
        var now = _clock.GetUtcNow();
        // A first contact: everything is new, which says nothing about a rate.
        await StoreAsync("fresh", now.AddMinutes(-10), "a", "b", "c", "d");
        var rate = new SignatureRate(_database, Options.Create(_options), _clock, _logger);

        await rate.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.Null(rate.NewSignaturesLastHour("fresh"));
        Assert.False(rate.IsAbnormal("fresh"));
    }

    [Fact]
    public async Task A_zero_threshold_turns_the_warning_off()
    {
        var now = _clock.GetUtcNow();
        await StoreAsync("noisy", now.AddDays(-1), "old");
        await StoreAsync("noisy", now.AddMinutes(-30), "a", "b", "c", "d");
        var options = _options with { NewSignaturesPerHourWarning = 0 };
        var rate = new SignatureRate(_database, Options.Create(options), _clock, _logger);

        await rate.EvaluateAsync(TestContext.Current.CancellationToken);

        Assert.False(rate.IsAbnormal("noisy"));
        Assert.Empty(_logger.Messages);
    }

    private async Task StoreAsync(string sourceId, DateTimeOffset firstSeen, params string[] signatures)
    {
        var findings = signatures.Select(signature => new ParsedFinding(
            $"{sourceId}-{signature}", "{}", "checkout", "excessive_fanout", "warning", "GET /orders",
            "template-hash", "trace", "daemon_production", 4, firstSeen.ToUnixTimeMilliseconds())).ToList();
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.True(await _database.TryUpsertBatchAsync(
            new SourceSnapshot(sourceId, sourceId, "production", "0.30.0"),
            new ParsedBatch(findings, 0),
            _clock.GetUtcNow().ToUnixTimeMilliseconds(),
            TestContext.Current.CancellationToken));
    }

    private static SourceOptions Daemon(string id)
    {
        return new SourceOptions
        {
            Id = id,
            Name = id,
            Environment = "production",
            Kind = SourceKinds.Daemon,
            BaseUrl = new Uri("http://127.0.0.1:1")
        };
    }
}
