using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     The daemon half of an ack taken on the Hub: the write, what its answer
///     means to the caller, and the mirror read again once the daemon took it.
///     The endpoint has judged the caller and the body before anything here runs.
/// </summary>
public sealed partial class AckRelay(
    DaemonClient client,
    HubDatabase database,
    AckReader reader,
    AckRelayGate gate,
    IOptions<HubOptions> options,
    TimeProvider timeProvider,
    ILogger<AckRelay> logger)
{
    public AckRelayGate Gate => gate;

    public Task<AckTarget> ReadTargetAsync(SourceOptions source, AckWrite write, CancellationToken cancellationToken)
    {
        return database.ReadAckTargetAsync(source.Id, write.Signature, cancellationToken);
    }

    public async Task<AckRelayAnswer> SendAsync(
        SourceOptions source,
        AckWrite write,
        string identity,
        CancellationToken cancellationToken)
    {
        // The request holds one of two gate slots for the whole of this, the
        // write and the mirror read, so a hung daemon costs a bounded wait.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.HttpTimeout * 3);
        var action = write.Revoke ? "revoke" : "ack";
        var actor = WireIdentity(identity);

        int status;
        try
        {
            status = (int)await client.SendAckAsync(
                source,
                write.Signature,
                write.Revoke ? null : DaemonBody(write, actor),
                write.Revoke ? actor : null,
                deadline.Token);
        }
        catch (Exception exception) when (IsDaemonFailure(exception, cancellationToken))
        {
            var unreachable = exception is HttpRequestException;
            LogRelayFailed(logger, action, source.Id, identity, unreachable ? "network_error" : "timeout");
            return unreachable
                ? new AckRelayAnswer(StatusCodes.Status502BadGateway, "The daemon could not be reached.",
                    ProblemCodes.DaemonUnreachable)
                : new AckRelayAnswer(StatusCodes.Status504GatewayTimeout, "The daemon did not answer in time.",
                    ProblemCodes.DaemonTimeout);
        }

        LogRelayed(logger, action, source.Id, identity, status);
        if (status is >= 200 and < 300)
            await RefreshMirrorAsync(source, deadline.Token, cancellationToken);
        return Translate(status, write.Revoke);
    }

    // The deadline above cancels like a caller does, and only the caller leaving is not a failure.
    private static bool IsDaemonFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException or DaemonTimeoutException ||
               (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
    }

    private static AckRelayAnswer Translate(int daemonStatus, bool revoke)
    {
        return daemonStatus switch
        {
            >= 200 and < 300 => new AckRelayAnswer(StatusCodes.Status204NoContent, null),
            StatusCodes.Status400BadRequest => new AckRelayAnswer(
                StatusCodes.Status400BadRequest, "The daemon refused the signature as not canonical.",
                ProblemCodes.DaemonRefusedSignature),
            // Never relayed as a 401: the launcher answers one by reloading into
            // a sign-in, and no sign-in mends a key the Hub holds.
            StatusCodes.Status401Unauthorized => new AckRelayAnswer(
                StatusCodes.Status502BadGateway, "The daemon refused the Hub's ack credential.",
                ProblemCodes.DaemonRefusedCredential),
            StatusCodes.Status404NotFound when revoke => new AckRelayAnswer(
                StatusCodes.Status404NotFound,
                "This finding is not acked at this daemon, or only by its CI baseline, which no API revokes.",
                ProblemCodes.DaemonAckNotFound),
            StatusCodes.Status409Conflict => new AckRelayAnswer(
                StatusCodes.Status409Conflict, "This finding is already acked at the daemon or by its CI baseline.",
                ProblemCodes.DaemonAlreadyAcked),
            StatusCodes.Status503ServiceUnavailable => new AckRelayAnswer(
                StatusCodes.Status503ServiceUnavailable, "Acknowledgments are disabled on this daemon.",
                ProblemCodes.DaemonAcksDisabled),
            StatusCodes.Status507InsufficientStorage => new AckRelayAnswer(
                StatusCodes.Status507InsufficientStorage, "The ack store of this daemon is full.",
                ProblemCodes.DaemonAckStoreFull),
            _ => new AckRelayAnswer(StatusCodes.Status502BadGateway, $"The daemon answered {daemonStatus}.",
                ProblemCodes.DaemonUnexpectedAnswer)
        };
    }

    /// <summary>
    ///     The daemon took the write, so whatever happens here the caller is
    ///     told so. A mirror that could not follow is stale until the next poll.
    /// </summary>
    private async Task RefreshMirrorAsync(
        SourceOptions source,
        CancellationToken deadline,
        CancellationToken cancellationToken)
    {
        try
        {
            var states = await database.QuerySourceStatesAsync(deadline);
            // A source that was only ever pushed to has no poll state, so no
            // version to tell whether its daemon lists its baseline.
            if (states.GetValueOrDefault(source.Id)?.ProducerVersion is { } producerVersion)
                await reader.ReadAsync(
                    source, producerVersion, timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), deadline);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogMirrorNotRefreshed(logger, exception, source.Id);
        }
    }

    // A header takes printable ASCII and loses its outer spaces on the way, so
    // any other identity travels percent-encoded, and in that one form on both
    // routes: the by of an ack and the X-User-Id of its revoke read the same.
    private static string WireIdentity(string identity)
    {
        return identity == identity.Trim() && identity.All(static character => character is >= ' ' and <= '~')
            ? identity
            : Uri.EscapeDataString(identity);
    }

    // Rebuilt field by field: nothing the caller sent travels as bytes, and
    // the expiry is written back from the instant it was read as.
    private static byte[] DaemonBody(AckWrite write, string by)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("by", by);
            writer.WriteString("reason", write.Reason);
            if (write.ExpiresAtMs is { } expiresAtMs)
                writer.WriteString(
                    "expires_at",
                    DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs)
                        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return [.. buffer.WrittenSpan];
    }

    [LoggerMessage(
        1310,
        LogLevel.Information,
        "Relayed {Action} to source {SourceId} for {Identity}: the daemon answered {Status}.")]
    private static partial void LogRelayed(ILogger logger, string action, string sourceId, string identity, int status);

    [LoggerMessage(
        1311,
        LogLevel.Warning,
        "Relaying {Action} to source {SourceId} for {Identity} failed: {ErrorCode}.")]
    private static partial void LogRelayFailed(
        ILogger logger,
        string action,
        string sourceId,
        string identity,
        string errorCode);

    [LoggerMessage(
        1312,
        LogLevel.Warning,
        "Source {SourceId} took the write and its ack mirror could not be read again.")]
    private static partial void LogMirrorNotRefreshed(ILogger logger, Exception exception, string sourceId);
}
