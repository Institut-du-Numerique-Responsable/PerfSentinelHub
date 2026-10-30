using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Cards;

/// <summary>
///     The signing key the engine makes on first use: its private file beside the database, its
///     public half and dates in card_keys, rotation and revocation.
/// </summary>
public sealed class CardKeysTests : IDisposable
{
    // 2026-10-30 at noon UTC.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeMilliseconds(1_793_361_600_000));

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-keys-{Guid.NewGuid():N}");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_workspace, "data", "hub.db");

    private string KeygenArgs => Path.Combine(_workspace, "reports", "card-keygen-args.txt");

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    public void Dispose()
    {
        TestPool.ClearFor(DatabasePath);
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task The_first_use_creates_a_key_only_the_hub_reads()
    {
        var (keys, database) = await KeysAsync();

        var key = await keys.CurrentAsync(Token);

        Assert.Equal("ps1:" + new string('A', 22), key.Fingerprint);
        Assert.Equal(new StoredCardKey(key.Fingerprint, new string('A', 43), NowMs, null, null),
            Assert.Single(await database.ReadCardKeysAsync(Token)));
        var file = Path.Combine(_workspace, "data", "card-keys", new string('A', 22) + ".pk8");
        Assert.Equal(file, keys.KeyFile(key));
        Assert.Equal("fake pkcs8", await File.ReadAllTextAsync(file, Token));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(file)!));
        }

        // The engine wrote into a private directory, gone once the key is in place.
        var arguments = (await File.ReadAllTextAsync(KeygenArgs, Token)).Trim().Split(' ');
        Assert.Equal(["card", "keygen", "--out-dir"], arguments[..3]);
        Assert.False(Directory.Exists(arguments[3]));
    }

    [Fact]
    public async Task The_second_use_reuses_it()
    {
        var (keys, _) = await KeysAsync();

        var first = await keys.CurrentAsync(Token);
        var second = await keys.CurrentAsync(Token);

        Assert.Equal(first, second);
        Assert.Single(await File.ReadAllLinesAsync(KeygenArgs, Token));
    }

    [Fact]
    public async Task Two_concurrent_first_uses_make_one_key()
    {
        var (keys, database) = await KeysAsync();

        var both = await Task.WhenAll(keys.CurrentAsync(Token), keys.CurrentAsync(Token));

        Assert.Equal(both[0], both[1]);
        Assert.Single(await database.ReadCardKeysAsync(Token));
        Assert.Single(await File.ReadAllLinesAsync(KeygenArgs, Token));
    }

    [Fact]
    public async Task A_missing_private_file_retires_the_key_and_makes_a_new_one()
    {
        var (keys, database) = await KeysAsync();
        var lost = await keys.CurrentAsync(Token);
        File.Delete(keys.KeyFile(lost));
        _clock.Advance(TimeSpan.FromDays(3));

        var made = await keys.CurrentAsync(Token);

        Assert.Equal("ps1:" + new string('A', 21) + "B", made.Fingerprint);
        Assert.True(File.Exists(keys.KeyFile(made)));
        Assert.Equal(
            [lost with { RetiredAtMs = NowMs }, made with { RetiredAtMs = null }],
            await database.ReadCardKeysAsync(Token));
    }

    [Fact]
    public async Task Rotation_retires_the_current_key_and_deletes_its_private_file()
    {
        var (keys, database) = await KeysAsync();
        var old = await keys.CurrentAsync(Token);
        _clock.Advance(TimeSpan.FromDays(1));

        var made = await keys.RotateAsync(Token);

        Assert.NotEqual(old.Fingerprint, made.Fingerprint);
        Assert.False(File.Exists(keys.KeyFile(old)));
        Assert.True(File.Exists(keys.KeyFile(made)));
        Assert.Equal([old with { RetiredAtMs = NowMs }, made], await database.ReadCardKeysAsync(Token));
        Assert.Equal(made, await keys.CurrentAsync(Token));
    }

    [Fact]
    public async Task Revoking_sets_the_date_and_retires_the_key()
    {
        var (keys, database) = await KeysAsync();
        var key = await keys.CurrentAsync(Token);
        _clock.Advance(TimeSpan.FromDays(3));

        var outcome = await keys.RevokeAsync(key.Fingerprint, new DateOnly(2026, 11, 1), Token);

        Assert.Equal(CardRevokeOutcome.Revoked, outcome);
        Assert.Equal(key with { RetiredAtMs = NowMs, RevokedOn = "2026-11-01" },
            Assert.Single(await database.ReadCardKeysAsync(Token)));
        Assert.False(File.Exists(keys.KeyFile(key)));
    }

    [Fact]
    public async Task A_retired_key_can_be_revoked()
    {
        var (keys, database) = await KeysAsync();
        var old = await keys.CurrentAsync(Token);
        _clock.Advance(TimeSpan.FromDays(1));
        var made = await keys.RotateAsync(Token);
        var retiredAtMs = NowMs;
        _clock.Advance(TimeSpan.FromDays(2));

        var outcome = await keys.RevokeAsync(old.Fingerprint, new DateOnly(2026, 10, 30), Token);

        Assert.Equal(CardRevokeOutcome.Revoked, outcome);
        Assert.Equal(
            [old with { RetiredAtMs = retiredAtMs, RevokedOn = "2026-10-30" }, made],
            await database.ReadCardKeysAsync(Token));
    }

    [Theory]
    [InlineData("2026-10-29", CardRevokeOutcome.BadDate)]
    [InlineData("2026-10-31", CardRevokeOutcome.BadDate)]
    [InlineData("2026-10-30", CardRevokeOutcome.Revoked)]
    public async Task A_revocation_before_the_key_existed_or_in_the_future_is_refused(
        string on,
        CardRevokeOutcome expected)
    {
        var (keys, database) = await KeysAsync();
        var key = await keys.CurrentAsync(Token);

        Assert.Equal(expected, await keys.RevokeAsync(key.Fingerprint, DateOnly.Parse(on), Token));
        Assert.Equal(CardRevokeOutcome.Unknown,
            await keys.RevokeAsync("ps1:" + new string('Z', 22), DateOnly.Parse(on), Token));
        Assert.Equal(expected == CardRevokeOutcome.Revoked ? on : null,
            Assert.Single(await database.ReadCardKeysAsync(Token)).RevokedOn);
    }

    [Theory]
    [InlineData("""{"fingerprint":"abc","public_key":"x"}""", 0)]
    [InlineData("not json", 0)]
    [InlineData("""{"fingerprint":"ps1:AAAAAAAAAAAAAAAAAAAAAA","public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""", 1)]
    public async Task An_engine_line_out_of_contract_makes_no_key(string line, int exitCode)
    {
        var (keys, database) = await KeysAsync(
            new FakeEngine { Card = true, CardKeygenOutput = line, CardKeygenExitCode = exitCode });

        var refused = await Assert.ThrowsAsync<CardEngineException>(() => keys.CurrentAsync(Token));

        Assert.Equal("engine_contract", refused.Reason);
        Assert.Empty(await database.ReadCardKeysAsync(Token));
        var directory = Path.Combine(_workspace, "data", "card-keys");
        Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
    }

    [Fact]
    public async Task An_engine_without_card_makes_no_key()
    {
        var (keys, database) = await KeysAsync(new FakeEngine());

        var refused = await Assert.ThrowsAsync<CardEngineException>(() => keys.CurrentAsync(Token));

        Assert.Equal("unsupported", refused.Reason);
        Assert.Empty(await database.ReadCardKeysAsync(Token));
    }

    private async Task<(CardKeys Keys, HubDatabase Database)> KeysAsync(FakeEngine? engine = null)
    {
        var enginePath = (engine ?? new FakeEngine { Card = true }).WriteTo(Path.Combine(_workspace, "engine"));
        var options = Options.Create(new HubOptions
        {
            DatabasePath = DatabasePath,
            Analysis = new AnalysisOptions
            {
                EngineBinaryPath = enginePath,
                ReportDirectory = Path.Combine(_workspace, "reports")
            }
        });
        var database = new HubDatabase(options, _clock);
        await database.InitializeAsync(Token);
        var probe = new EngineProbe(options, NullLogger<EngineProbe>.Instance);
        await probe.StartAsync(Token);
        return (new CardKeys(database, probe, options, _clock), database);
    }
}
