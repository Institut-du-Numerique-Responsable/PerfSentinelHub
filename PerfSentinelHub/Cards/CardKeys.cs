using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Cards;

/// <summary>
///     The share card signing key. The engine makes it (<c>card keygen</c>) on first use; its private
///     file sits in <c>card-keys/</c> beside the database, so outside SQLite and outside backup, and its
///     public half and dates in <c>card_keys</c>. One key is current at most. A key whose private file
///     is gone is retired at next use and a new one made; a retired or revoked key loses its file.
/// </summary>
public sealed partial class CardKeys(
    HubDatabase database,
    EngineProbe engine,
    IOptions<HubOptions> options,
    TimeProvider timeProvider)
{
    private const string FingerprintPrefix = "ps1:";
    private const string EngineContract = "engine_contract";
    private const int MaxLineBytes = 4096;
    private static readonly TimeSpan KeygenTimeout = TimeSpan.FromSeconds(30);

    // One key at a time: two first cards at once must not make two keys.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string KeysDirectory =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath))!, "card-keys");

    private long NowMs => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    public string KeyFile(StoredCardKey key)
    {
        return Path.Combine(KeysDirectory, key.Fingerprint[FingerprintPrefix.Length..] + ".pk8");
    }

    /// <summary>The current key, made on first use or when its private file has gone.</summary>
    public async Task<StoredCardKey> CurrentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await CurrentRowAsync(cancellationToken) is { } current)
            {
                if (File.Exists(KeyFile(current)))
                    return current;
                // Restored from backup on a fresh volume: earlier cards keep the public key and its end date.
                await database.RetireCardKeyAsync(current.Fingerprint, NowMs, cancellationToken);
            }

            return await MakeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredCardKey> RotateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await CurrentRowAsync(cancellationToken) is { } current)
            {
                await database.RetireCardKeyAsync(current.Fingerprint, NowMs, cancellationToken);
                File.Delete(KeyFile(current));
            }

            return await MakeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     Revokes a key from the first day it may have leaked, which lies between the key's creation
    ///     and today (UTC). A retired key keeps its end date.
    /// </summary>
    public async Task<CardRevokeOutcome> RevokeAsync(string fingerprint, DateOnly on,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var keys = await database.ReadCardKeysAsync(cancellationToken);
            if (keys.FirstOrDefault(key => key.Fingerprint == fingerprint) is not { } known)
                return CardRevokeOutcome.Unknown;
            var nowMs = NowMs;
            if (on < Day(known.CreatedAtMs) || on > Day(nowMs))
                return CardRevokeOutcome.BadDate;
            // Known under the gate, and a key row is never deleted, so the update finds it.
            await database.RevokeCardKeyAsync(fingerprint,
                on.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), nowMs, cancellationToken);
            File.Delete(KeyFile(known));
            return CardRevokeOutcome.Revoked;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoredCardKey?> CurrentRowAsync(CancellationToken cancellationToken)
    {
        return (await database.ReadCardKeysAsync(cancellationToken)).LastOrDefault(key => key.RetiredAtMs is null);
    }

    private async Task<StoredCardKey> MakeAsync(CancellationToken cancellationToken)
    {
        var analysis = options.Value.Analysis;
        if (analysis.EngineBinaryPath is not { } binary || !engine.SupportsCard)
            throw new CardEngineException("unsupported");

        var directory = Directory.CreateTempSubdirectory("perf-sentinel-hub-card-");
        try
        {
            var result = await KeygenAsync(binary, directory.FullName, analysis.ReportDirectory, cancellationToken);
            var keyFiles = directory.GetFiles("*.pk8");
            if (result.ExitCode != 0 || Parse(result.StandardOutput, NowMs) is not { } key || keyFiles.Length != 1)
                throw new CardEngineException(EngineContract);

            // The row first: a file that then fails to land leaves a current key without its file,
            // which the next use retires.
            await database.InsertCardKeyAsync(key, cancellationToken);
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(KeysDirectory);
            else
                Directory.CreateDirectory(KeysDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = KeyFile(key);
            File.Move(keyFiles[0].FullName, file);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return key;
        }
        finally
        {
            try
            {
                directory.Delete(true);
            }
            catch (IOException)
            {
                // A file still held open: the operating system's temporary cleanup takes it.
            }
        }
    }

    private static async Task<EngineResult> KeygenAsync(string binary, string outDirectory, string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(KeygenTimeout);
        try
        {
            return await EngineProcess.RunAsync(binary, ["card", "keygen", "--out-dir", outDirectory],
                MaxLineBytes, workingDirectory, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CardEngineException("timeout");
        }
        catch (EngineOutputTooLargeException)
        {
            throw new CardEngineException(EngineContract);
        }
    }

    // The C2 line {"fingerprint", "public_key"}; anything out of the key shapes makes no key.
    private static StoredCardKey? Parse(byte[] output, long nowMs)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("fingerprint", out var fingerprint) &&
                   fingerprint.ValueKind == JsonValueKind.String &&
                   FingerprintShape().IsMatch(fingerprint.GetString()!) &&
                   root.TryGetProperty("public_key", out var publicKey) &&
                   publicKey.ValueKind == JsonValueKind.String &&
                   PublicKeyShape().IsMatch(publicKey.GetString()!)
                ? new StoredCardKey(fingerprint.GetString()!, publicKey.GetString()!, nowMs, null, null)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateOnly Day(long atMs)
    {
        return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(atMs).UtcDateTime);
    }

    [GeneratedRegex(@"\Aps1:[A-Za-z0-9_-]{22}\z")]
    private static partial Regex FingerprintShape();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    private static partial Regex PublicKeyShape();
}
