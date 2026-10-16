using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Maintenance;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     Published disclosures, their share links and their webhook deliveries, as the store
///     keeps them: files byte for byte in BLOBs, never read by a listing.
/// </summary>
public sealed class DisclosureStorageTests : IAsyncLifetime
{
    private const long NowMs = 1_800_000_000_000;

    private readonly string _backupPath =
        Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-dbak-{Guid.NewGuid():N}.db");

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-disc-{Guid.NewGuid():N}.db");
    private HubDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _database = Open();
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        foreach (var path in new[] { _path, _backupPath })
        {
            TestPool.ClearFor(path);
            File.Delete(path);
            File.Delete($"{path}-shm");
            File.Delete($"{path}-wal");
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_disclosure_round_trips_its_files_byte_for_byte()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Spacing and key order a re-serialisation would lose.
        var report = Encoding.UTF8.GetBytes("{ \"z\" : 1,\n  \"a\": \"é\" }\n");
        var record = Record("d1", "uuid-1") with
        {
            Files = new DisclosureFiles(report, [1, 2, 3], null),
            Pdf = Encoding.ASCII.GetBytes("%PDF-1.7 fake")
        };

        Assert.True(await _database.InsertDisclosureAsync(record, cancellationToken));

        Assert.Equal(report, await _database.ReadFileAsync("d1", DisclosureFile.Report, cancellationToken));
        Assert.Equal([1, 2, 3], await _database.ReadFileAsync("d1", DisclosureFile.Attestation, cancellationToken));
        Assert.Null(await _database.ReadFileAsync("d1", DisclosureFile.Bundle, cancellationToken));
        Assert.Equal(record.Pdf, await _database.ReadFileAsync("d1", DisclosureFile.Pdf, cancellationToken));
        var summary = await _database.GetDisclosureAsync("d1", cancellationToken);
        Assert.NotNull(summary);
        Assert.True(summary.HasAttestation);
        Assert.False(summary.HasBundle);
        Assert.True(summary.HasPdf);
        Assert.Equal("""{"energy_kwh":12.5}""", summary.FiguresJson);
        Assert.Equal("d1", (await _database.FindDisclosureByUuidAsync("uuid-1", cancellationToken))?.Id);
    }

    [Fact]
    public async Task A_report_uuid_is_stored_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), cancellationToken));
        Assert.False(await _database.InsertDisclosureAsync(Record("d2", "uuid-1"), cancellationToken));

        Assert.Single(await _database.ListDisclosuresAsync(cancellationToken));
    }

    [Fact]
    public async Task The_list_comes_newest_period_first_and_carries_no_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("q1", "u1") with { PeriodTo = "2026-03-31" }, cancellationToken);
        await _database.InsertDisclosureAsync(Record("q3", "u3") with { PeriodTo = "2026-09-30" }, cancellationToken);
        await _database.InsertDisclosureAsync(Record("q2", "u2") with { PeriodTo = "2026-06-30" }, cancellationToken);

        var list = await _database.ListDisclosuresAsync(cancellationToken);

        Assert.Equal(["q3", "q2", "q1"], list.Select(item => item.Id));
        Assert.DoesNotContain(typeof(DisclosureSummary).GetProperties(),
            property => property.PropertyType == typeof(byte[]));
    }

    [Fact]
    public async Task A_signed_copy_replaces_the_unsigned_one_and_says_when()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), cancellationToken);
        var signed = new DisclosureFiles(Encoding.UTF8.GetBytes("{\"signed\":true}"), [7], [8]);

        await _database.ReplaceSignedCopyAsync("d1", signed, "signed-with-attestation", (new byte[] { 9 }, null),
            "0.31.0", NowMs + 5,
            cancellationToken);

        var summary = (await _database.GetDisclosureAsync("d1", cancellationToken))!;
        Assert.Equal(NowMs + 5, summary.SignatureAddedAtMs);
        Assert.Equal("signed-with-attestation", summary.IntegrityLevel);
        Assert.True(summary.HasBundle);
        Assert.Equal(signed.Report, await _database.ReadFileAsync("d1", DisclosureFile.Report, cancellationToken));
        Assert.Equal([9], await _database.ReadFileAsync("d1", DisclosureFile.Pdf, cancellationToken));
    }

    [Fact]
    public async Task Rendering_again_sets_or_clears_the_pdf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(
            Record("d1", "uuid-1") with { Pdf = null, RenderReason = "unsupported_engine" }, cancellationToken);

        await _database.SetPdfAsync("d1", [1], null, "0.31.0", true, true, cancellationToken);

        var summary = (await _database.GetDisclosureAsync("d1", cancellationToken))!;
        Assert.True(summary.HasPdf);
        Assert.Null(summary.RenderReason);
        Assert.Equal("0.31.0", summary.EngineVersion);
        Assert.True(summary.HashChecked);
    }

    [Fact]
    public async Task A_withdrawn_disclosure_kills_its_share_links()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), cancellationToken);
        var hash = Hash("pshare_one");
        await _database.InsertShareLinkAsync(Link("l1", "d1", hash), cancellationToken);
        Assert.Equal(("l1", "d1"), await _database.FindShareLinkAsync(hash, NowMs, cancellationToken));

        Assert.True(await _database.WithdrawAsync("d1", "wrong quarter", "alice", NowMs, cancellationToken));

        Assert.Null(await _database.FindShareLinkAsync(hash, NowMs, cancellationToken));
        var summary = (await _database.GetDisclosureAsync("d1", cancellationToken))!;
        Assert.Equal(NowMs, summary.WithdrawnAtMs);
        Assert.Equal("wrong quarter", summary.WithdrawnReason);
        Assert.Equal("alice", summary.WithdrawnBy);
        // Withdrawn once: a second withdrawal changes nothing.
        Assert.False(await _database.WithdrawAsync("d1", "again", "bob", NowMs + 1, cancellationToken));
    }

    [Fact]
    public async Task A_publication_and_a_withdrawal_each_land_with_their_events_and_only_once()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), ["auditors"], cancellationToken));
        Assert.False(await _database.InsertDisclosureAsync(Record("d2", "uuid-1"), ["auditors"], cancellationToken));
        Assert.True(await _database.WithdrawAsync("d1", "wrong", "alice", NowMs, ["auditors"], cancellationToken));
        Assert.False(await _database.WithdrawAsync("d1", "again", "bob", NowMs, ["auditors"], cancellationToken));

        Assert.Equal(["published", "withdrawn"],
            (await _database.ListDeliveriesAsync("d1", cancellationToken)).Select(delivery => delivery.Event));
        Assert.Empty(await _database.ListDeliveriesAsync("d2", cancellationToken));
    }

    [Fact]
    public async Task An_expired_or_revoked_link_is_not_found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), cancellationToken);
        await _database.InsertShareLinkAsync(Link("expired", "d1", Hash("a")) with { ExpiresAtMs = NowMs },
            cancellationToken);
        await _database.InsertShareLinkAsync(Link("revoked", "d1", Hash("b")), cancellationToken);

        Assert.True(await _database.RevokeShareLinkAsync("d1", "revoked", "alice", NowMs, cancellationToken));

        Assert.Null(await _database.FindShareLinkAsync(Hash("a"), NowMs, cancellationToken));
        Assert.Null(await _database.FindShareLinkAsync(Hash("b"), NowMs, cancellationToken));
        Assert.Null(await _database.FindShareLinkAsync(Hash("unknown"), NowMs, cancellationToken));
        var links = await _database.ListShareLinksAsync("d1", cancellationToken);
        Assert.Equal("alice", Assert.Single(links, link => link.Id == "revoked").RevokedBy);
    }

    [Fact]
    public async Task Download_counts_add_up()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), cancellationToken);
        await _database.InsertShareLinkAsync(Link("l1", "d1", Hash("a")), cancellationToken);

        await _database.AddShareDownloadsAsync(
            new Dictionary<string, ShareDownloads> { ["l1"] = new(3, NowMs + 1, null) }, cancellationToken);
        await _database.AddShareDownloadsAsync(
            new Dictionary<string, ShareDownloads> { ["l1"] = new(2, NowMs + 2, "10.0.0.7") }, cancellationToken);

        var link = Assert.Single(await _database.ListShareLinksAsync("d1", cancellationToken));
        Assert.Equal(5, link.Downloads);
        Assert.Equal(NowMs + 2, link.LastUsedAtMs);
        Assert.Equal("10.0.0.7", link.LastClient);
    }

    [Fact]
    public async Task Due_deliveries_come_back_after_a_reopen_and_leave_once_settled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("d1", "uuid-1"), cancellationToken);
        await _database.EnqueueDeliveriesAsync("d1", "published", ["auditors", "mail"], NowMs, cancellationToken);

        var reopened = Open();
        await reopened.InitializeAsync(cancellationToken);
        var due = await reopened.DueDeliveriesAsync(NowMs, cancellationToken);

        Assert.Equal(["auditors", "mail"], due.Select(delivery => delivery.Webhook).Order(StringComparer.Ordinal));
        Assert.All(due, delivery => Assert.Equal("published", delivery.Event));
        await reopened.RecordDeliveryAttemptAsync(due[0].Id, 0, 204, DeliveryStates.Delivered, NowMs, null,
            cancellationToken);
        await reopened.RecordDeliveryAttemptAsync(due[1].Id, 0, 500, DeliveryStates.Pending, NowMs, NowMs + 60_000,
            cancellationToken);
        Assert.Empty(await reopened.DueDeliveriesAsync(NowMs + 59_999, cancellationToken));
        var retried = Assert.Single(await reopened.DueDeliveriesAsync(NowMs + 60_000, cancellationToken));
        Assert.Equal(1, retried.Attempts);
        Assert.Equal(500, retried.LastStatus);
        Assert.Equal(2, (await reopened.ListDeliveriesAsync("d1", cancellationToken)).Count);
    }

    [Fact]
    public async Task A_backup_carries_the_files()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _database.InsertDisclosureAsync(Record("d1", "uuid-1") with { Pdf = [37, 80, 68, 70] },
            cancellationToken);

        Assert.Equal(0, await HubBackup.RunAsync(_path, _backupPath, cancellationToken));

        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _backupPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pdf FROM disclosures WHERE id = 'd1';";
        Assert.Equal([37, 80, 68, 70], (byte[])(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    private HubDatabase Open()
    {
        return new HubDatabase(Options.Create(new HubOptions { DatabasePath = _path }), TimeProvider.System);
    }

    private static DisclosureRecord Record(string id, string uuid)
    {
        return new DisclosureRecord(
            id, uuid, "Acme", "quarterly", "2026-07-01", "2026-09-30", "official", "public",
            "sha256:" + new string('a', 64), "G2", """{"energy_kwh":12.5}""",
            new DisclosureFiles("{}"u8.ToArray(), null, null), [1], null, "0.31.0", true, null, NowMs);
    }

    private static ShareLinkRecord Link(string id, string disclosureId, byte[] tokenHash)
    {
        return new ShareLinkRecord(id, disclosureId, tokenHash, "alice", NowMs, NowMs + 86_400_000);
    }

    private static byte[] Hash(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}
