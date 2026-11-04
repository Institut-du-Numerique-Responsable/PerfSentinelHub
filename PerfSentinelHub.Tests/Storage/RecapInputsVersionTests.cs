using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Storage;

/// <summary>What moves the version a kept recap is reused under, and what leaves it alone.</summary>
public sealed class RecapInputsVersionTests : IAsyncLifetime
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-inputs-{Guid.NewGuid():N}.db");

    private HubDatabase _database = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new HubDatabase(
            Options.Create(new HubOptions { DatabasePath = _path }),
            new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000)));
        await _database.InitializeAsync(Token);
    }

    public ValueTask DisposeAsync()
    {
        TestPool.ClearFor(_path);
        File.Delete(_path);
        File.Delete($"{_path}-shm");
        File.Delete($"{_path}-wal");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_action_and_a_label_move_it()
    {
        var start = _database.RecapInputsVersion;

        await _database.CreateActionAsync("sig", "todo", null, null, "alice", 1_000, null, Token);
        var afterCreate = _database.RecapInputsVersion;
        await _database.UpdateActionAsync("sig", "in_progress", null, false, "alice", 2_000, null, Token);
        var afterUpdate = _database.RecapInputsVersion;
        await _database.SetEndpointLabelAsync("svc", "GET /a", "Orders", "alice", Token);
        var afterLabel = _database.RecapInputsVersion;

        Assert.True(afterCreate > start);
        Assert.True(afterUpdate > afterCreate);
        Assert.True(afterLabel > afterUpdate);
    }

    [Fact]
    public async Task A_source_listing_new_acks_moves_it_and_the_same_listing_does_not()
    {
        var acks = new[] { new ParsedAck("sig", "daemon", "alice", null, "2026-11-04T10:00:00Z", null, null) };
        await _database.ReplaceSourceAcksAsync("a", acks, AckReadStates.Ok, 1_000, Token);
        var listed = _database.RecapInputsVersion;

        await _database.ReplaceSourceAcksAsync("a", acks, AckReadStates.Ok, 2_000, Token);
        Assert.Equal(listed, _database.RecapInputsVersion);

        await _database.ReplaceSourceAcksAsync("a", [], AckReadStates.Ok, 3_000, Token);
        Assert.True(_database.RecapInputsVersion > listed);
    }
}
