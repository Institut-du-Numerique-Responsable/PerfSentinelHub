using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Access;

/// <summary>
///     Under a narrowed scope the names a recap offers reveal nothing past it. The grouping and attribute
///     rows come through the write path, so the tests also hold finding_groupings.grouping and
///     finding_attributes.grouping_value to the same value.
/// </summary>
public sealed class RecapFilterScopeTests : IAsyncLifetime
{
    private const string Namespace = "k8s.namespace.name";

    private static readonly RecapPeriod Week = RecapPeriod.Of(RecapPeriodKind.Week, new DateOnly(2026, 10, 29));

    private readonly FakeTimeProvider _clock =
        new(DateTimeOffset.Parse("2026-10-29T10:00:00Z", CultureInfo.InvariantCulture));

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-names-{Guid.NewGuid():N}.db");

    private HubDatabase _database = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), _clock);
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

    // The copy is visible through x; that says nothing of y, where it was seen too.
    [Fact]
    public async Task A_scope_on_one_namespace_never_offers_another_the_finding_was_seen_in()
    {
        await SeedAsync("a", "f", "svc", """{"key":"k8s.namespace.name","value":"x"}""");
        await SeedAsync("a", "f", "svc", """{"key":"k8s.namespace.name","value":"y"}""");

        Assert.Equal(["x"], await GroupingsAsync(new PatternOptions { Attribute = $"{Namespace}=x" }));
    }

    // Proved under y by the pair the scope names; z carries another team.
    [Fact]
    public async Task A_scope_on_another_key_offers_only_the_namespaces_that_carry_its_pair()
    {
        await SeedAsync("a", "f", "svc",
            """{"key":"k8s.namespace.name","value":"y"}""", """{"key":"team","value":"blue"}""");
        await SeedAsync("a", "f", "svc",
            """{"key":"k8s.namespace.name","value":"z"}""", """{"key":"team","value":"red"}""");

        Assert.Equal(["y"], await GroupingsAsync(new PatternOptions { Attribute = "team=blue" }));
    }

    [Fact]
    public async Task A_scope_with_no_key_offers_every_namespace_of_the_copies_it_reads()
    {
        await SeedAsync("a", "f", "svc", """{"key":"k8s.namespace.name","value":"x"}""");
        await SeedAsync("a", "f", "svc", """{"key":"k8s.namespace.name","value":"y"}""");
        await SeedAsync("b", "f", "svc", """{"key":"k8s.namespace.name","value":"other-source"}""");
        await SeedAsync("a", "g", "other-svc", """{"key":"k8s.namespace.name","value":"other-service"}""");

        Assert.Equal(["x", "y"], await GroupingsAsync(new PatternOptions { Source = "a", Service = "svc" }));
    }

    // One pattern must hold the source and the pair: blue at b and a at red prove nothing together.
    [Fact]
    public async Task A_pattern_proves_a_namespace_with_all_its_parts_at_once()
    {
        await SeedAsync("a", "f", "svc",
            """{"key":"k8s.namespace.name","value":"y"}""", """{"key":"team","value":"blue"}""");
        await SeedAsync("a", "f", "svc",
            """{"key":"k8s.namespace.name","value":"z"}""", """{"key":"team","value":"red"}""");
        await SeedAsync("b", "f", "svc",
            """{"key":"k8s.namespace.name","value":"w"}""", """{"key":"team","value":"blue"}""");

        Assert.Equal(["y"], await GroupingsAsync(new PatternOptions { Source = "a", Attribute = "team=blue" }));
    }

    [Fact]
    public async Task A_service_out_of_scope_is_not_offered_even_on_the_same_finding()
    {
        await SeedAsync("a", "f", "svc-a");
        await SeedAsync("b", "f", "svc-b");
        await SeedAsync("a", "g", "svc-c");

        Assert.Equal(["svc-a", "svc-c"], await ServicesAsync(new PatternOptions { Source = "a" }));
        Assert.Equal(["svc-c"], await ServicesAsync(new PatternOptions { Service = "svc-c" }));
    }

    [Fact]
    public async Task The_sources_a_read_names_cut_a_narrowed_scope_further()
    {
        await SeedAsync("a", "f", "svc-a", """{"key":"k8s.namespace.name","value":"ns-a"}""");
        await SeedAsync("b", "g", "svc-b", """{"key":"k8s.namespace.name","value":"ns-b"}""");
        var both = Scope(new PatternOptions { Service = "svc-*" });

        Assert.Equal(["svc-a"], await _database.QueryRecapServicesAsync(Week, ["a"], both, Token));
        Assert.Equal(["ns-a"], await _database.QueryRecapGroupingsAsync(Week, ["a"], both, Token));
    }

    // A copy with no service of its own: the shared row names the last writer's, from any source.
    [Fact]
    public async Task A_copy_with_no_service_of_its_own_does_not_offer_the_shared_one()
    {
        await SeedAsync("a", "f", "svc-a");
        await SeedAsync("b", "f", "svc-of-b");
        await using var connection = await _database.OpenConnectionAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE finding_sources SET service = NULL WHERE source_id = 'a';";
        await command.ExecuteNonQueryAsync(Token);

        Assert.Empty(await ServicesAsync(new PatternOptions { Source = "a" }));
    }

    // The two tables read the same field by two parsers: an escape must not separate them.
    [Fact]
    public async Task A_namespace_written_with_an_escape_still_meets_its_attribute_row()
    {
        await SeedAsync("a", "f", "svc",
            """{"key":"k8s.namespace.name","value":"café"}""", """{"key":"team","value":"blue"}""");

        Assert.Equal(["café"], await GroupingsAsync(new PatternOptions { Attribute = "team=blue" }));
    }

    // Both shapes reach finding_groupings and write no attribute row under the value: nothing proves
    // them to a keyed scope, which reads the copy through y, and no key means no proof is asked.
    [Fact]
    public async Task A_namespace_with_no_attribute_row_is_offered_only_to_a_scope_with_no_key()
    {
        var tooLong = new string('l', 257);
        await SeedAsync("a", "f", "svc",
            """{"key":"k8s.namespace.name","value":"y"}""", """{"key":"team","value":"blue"}""");
        await SeedAsync("a", "f", "svc", """{"value":"no-key"}""");
        await SeedAsync("a", "f", "svc", $$"""{"key":"k8s.namespace.name","value":"{{tooLong}}"}""");

        Assert.Equal(["y"], await GroupingsAsync(new PatternOptions { Attribute = "team=blue" }));
        Assert.Equal([tooLong, "no-key", "y"], await GroupingsAsync(new PatternOptions { Source = "a" }));
    }

    private static AccessScope Scope(PatternOptions pattern)
    {
        return AccessScope.Of([AccessPattern.Compile(pattern)]);
    }

    private Task<IReadOnlyList<string>> ServicesAsync(PatternOptions pattern)
    {
        return _database.QueryRecapServicesAsync(Week, null, Scope(pattern), Token);
    }

    private Task<IReadOnlyList<string>> GroupingsAsync(PatternOptions pattern)
    {
        return _database.QueryRecapGroupingsAsync(Week, null, Scope(pattern), Token);
    }

    // Each observation a millisecond after the last, as a poll would be, on the day of the clock.
    private Task SeedAsync(string source, string signature, string service, params string[] grouping)
    {
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        return _database.UpsertBatchAsync(
            new SourceSnapshot(source, source, "prod", "0.37.1"),
            FindingJson.Batch(FindingJson.Envelope(signature, service: service,
                grouping: $"[{string.Join(',', grouping)}]")),
            _clock.GetUtcNow().ToUnixTimeMilliseconds(), Token);
    }
}
