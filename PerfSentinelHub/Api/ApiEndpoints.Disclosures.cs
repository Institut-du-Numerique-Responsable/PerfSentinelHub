using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

public static partial class ApiEndpoints
{
    private const int MaxDisclosureBodyBytes = 8 * 1024 * 1024;
    private const string UnsupportedEngine = "unsupported_engine";
    private static readonly TimeSpan DisclosureImportBudget = TimeSpan.FromSeconds(60);
    private static readonly string[] DisclosureParts = ["report", "attestation", "bundle"];

    /// <summary>
    ///     A CI publishes a disclosure: the report, and its attestation and bundle when signed.
    ///     The engine checks and renders it before anything is stored, and a refusal stores nothing.
    /// </summary>
    // Every parameter is a collaborator the router injects.
#pragma warning disable S107
    private static async Task<IResult> ImportDisclosuresAsync(
        HttpRequest request,
        HubDatabase database,
        DisclosureImportGate gate,
        DisclosureMetrics metrics,
        DisclosureRenderer renderer,
        EngineProbe engine,
        IOptions<HubOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
#pragma warning restore S107
    {
        var disclosures = options.Value.Disclosures;
        if (AdmitDisclosure(request, disclosures, gate, metrics, out var boundary) is { } refused)
            return refused;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(DisclosureImportBudget);
            var body = await ReadBodyAsync(request, budget.Token, MaxDisclosureBodyBytes);
            if (body is null)
                return metrics.Refuse(DisclosureRejection.TooLarge,
                    TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));
            if (await ReadDisclosurePartsAsync(body, boundary, budget.Token) is not { } files ||
                !DisclosureEnvelope.TryParse(files.Report, out var envelope))
                return Rejected(metrics, DisclosureRejection.MalformedReport, "malformed_report");
            if (envelope.Internal && !disclosures.AllowInternal)
                return Rejected(metrics, DisclosureRejection.InternalRefused, "internal_refused");

            var publication = new Publication(database, renderer, engine, metrics, timeProvider, budget.Token);
            return await database.FindDisclosureByUuidAsync(envelope.ReportUuid, budget.Token) is { } existing
                ? await publication.RepublishAsync(existing, envelope, files)
                : await publication.PublishAsync(envelope, files, disclosures, loggerFactory.CreateLogger("DisclosureImport"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TypedResults.StatusCode(StatusCodes.Status408RequestTimeout);
        }
        finally
        {
            gate.Exit();
        }
    }

    // The key first, so a caller without it learns nothing else about the route.
    private static IResult? AdmitDisclosure(
        HttpRequest request,
        DisclosuresOptions disclosures,
        DisclosureImportGate gate,
        DisclosureMetrics metrics,
        out string boundary)
    {
        boundary = "";
        if (!disclosures.Enabled)
            return TypedResults.NotFound();
        if (!IsAuthorized(request, disclosures.ImportKey))
            return metrics.Refuse(DisclosureRejection.Unauthorized, TypedResults.Unauthorized());
        if (request.ContentLength > MaxDisclosureBodyBytes)
            return metrics.Refuse(DisclosureRejection.TooLarge,
                TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge));
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
            HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is not { Length: > 0 and <= 70 } parsed)
            return metrics.Refuse(DisclosureRejection.UnsupportedMediaType,
                TypedResults.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        if (!gate.TryEnter())
        {
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return metrics.Refuse(DisclosureRejection.GateFull,
                TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }

        boundary = parsed;
        return null;
    }

    /// <summary>
    ///     The three named parts, each exactly as sent, or null for anything else: a part
    ///     twice, a part the route does not take, an empty one, or no report.
    /// </summary>
    private static async Task<DisclosureFiles?> ReadDisclosurePartsAsync(
        byte[] body,
        string boundary,
        CancellationToken cancellationToken)
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(body, false);
            var reader = new MultipartReader(boundary, stream) { HeadersCountLimit = 8, HeadersLengthLimit = 8 * 1024 };
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    // With or without a file name: curl -F sends one, a hand-built body may not.
                    !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
                    HeaderUtilities.RemoveQuotes(disposition.Name).Value is not { } name ||
                    !DisclosureParts.Contains(name) || parts.ContainsKey(name))
                    return null;
                using var bytes = new MemoryStream();
                await section.Body.CopyToAsync(bytes, cancellationToken);
                if (bytes.Length == 0)
                    return null;
                parts[name] = bytes.ToArray();
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }

        return parts.TryGetValue("report", out var report)
            ? new DisclosureFiles(report, parts.GetValueOrDefault("attestation"), parts.GetValueOrDefault("bundle"))
            : null;
    }

    private static IResult Rejected(DisclosureMetrics metrics, DisclosureRejection rejection, string reason)
    {
        return metrics.Refuse(rejection, TypedResults.Json(
            new DisclosureRefusal(reason), HubJsonContext.Default.DisclosureRefusal,
            statusCode: StatusCodes.Status422UnprocessableEntity));
    }

    // Another report under a published uuid: a new report gets a new uuid.
    private static IResult Conflict(DisclosureMetrics metrics)
    {
        return metrics.Refuse(DisclosureRejection.Conflict, TypedResults.Json(
            new DisclosureRefusal("conflict"), HubJsonContext.Default.DisclosureRefusal,
            statusCode: StatusCodes.Status409Conflict));
    }

    private static IResult Accepted(DisclosureSummary summary, int status)
    {
        return TypedResults.Json(
            new DisclosureImportResponse(
                summary.Id, summary.HasPdf ? "rendered" : "not_checked", summary.RenderReason,
                summary.HashChecked == true, summary.BindingChecked),
            HubJsonContext.Default.DisclosureImportResponse,
            statusCode: status);
    }

    private static bool SameBytes(byte[]? stored, byte[]? received)
    {
        return stored is null ? received is null : received is not null && stored.AsSpan().SequenceEqual(received);
    }

    // A signed copy keeps each signature file the stored one has, byte for byte, and adds one
    // it lacks: --emit-attestation sends the attestation first, cosign the bundle later.
    private static bool AddsSignature(byte[]? attestation, byte[]? bundle, DisclosureFiles files)
    {
        return (attestation is null || SameBytes(attestation, files.Attestation)) &&
               (bundle is null || SameBytes(bundle, files.Bundle)) &&
               ((attestation is null && files.Attestation is not null) || (bundle is null && files.Bundle is not null));
    }

    /// <summary>One publication's collaborators, so its two paths read as two short methods.</summary>
    private sealed partial class Publication(
        HubDatabase database,
        DisclosureRenderer renderer,
        EngineProbe engine,
        DisclosureMetrics metrics,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        public async Task<IResult> PublishAsync(
            DisclosureEnvelope envelope,
            DisclosureFiles files,
            DisclosuresOptions disclosures,
            ILogger logger)
        {
            var outcome = await renderer.RenderAsync(files, cancellationToken);
            if (Refusal(outcome) is { } refusal)
                return refusal;

            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var (pdf, reason) = Rendering(outcome);
            var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            // Internal reports never leave the Hub, and a webhook takes public ones only.
            string[] webhooks = envelope.Internal
                ? []
                : [.. disclosures.Webhooks.Where(webhook => webhook.Confidentiality == "public").Select(webhook => webhook.Name)];
            if (!await database.InsertDisclosureAsync(
                    new DisclosureRecord(
                        id, envelope.ReportUuid, envelope.Organisation, envelope.PeriodType, envelope.PeriodFrom,
                        envelope.PeriodTo, envelope.Intent, envelope.Confidentiality, envelope.ContentHash,
                        envelope.IntegrityLevel, envelope.FiguresJson, files, pdf, reason, engine.Version,
                        pdf is null ? null : true, pdf is null || files.Attestation is null ? null : true, nowMs),
                    webhooks, cancellationToken))
                return Conflict(metrics);

            LogPublished(logger, id, envelope.Organisation, envelope.PeriodFrom, envelope.PeriodTo, pdf is not null);
            return Accepted((await database.GetDisclosureAsync(id, cancellationToken))!, StatusCodes.Status201Created);
        }

        /// <summary>
        ///     The same uuid again: the same bytes change nothing, a signed copy of an unsigned
        ///     report replaces it with no second event, and anything else is a conflict.
        /// </summary>
        public async Task<IResult> RepublishAsync(DisclosureSummary existing, DisclosureEnvelope envelope, DisclosureFiles files)
        {
            if (existing.ContentHash != envelope.ContentHash)
                return Conflict(metrics);
            var attestation = await database.ReadFileAsync(existing.Id, DisclosureFile.Attestation, cancellationToken);
            var bundle = await database.ReadFileAsync(existing.Id, DisclosureFile.Bundle, cancellationToken);
            if (SameBytes(await database.ReadFileAsync(existing.Id, DisclosureFile.Report, cancellationToken), files.Report) &&
                SameBytes(attestation, files.Attestation) && SameBytes(bundle, files.Bundle))
                return Accepted(existing, StatusCodes.Status200OK);
            if (!AddsSignature(attestation, bundle, files))
                return Conflict(metrics);

            var outcome = await renderer.RenderAsync(files, cancellationToken);
            if (Refusal(outcome) is { } refusal)
                return refusal;
            var (pdf, reason) = Rendering(outcome);
            await database.ReplaceSignedCopyAsync(
                existing.Id, files, envelope.IntegrityLevel, pdf, reason, engine.Version,
                timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
            return Accepted((await database.GetDisclosureAsync(existing.Id, cancellationToken))!, StatusCodes.Status200OK);
        }

        private IResult? Refusal(RenderOutcome outcome)
        {
            return outcome switch
            {
                RenderOutcome.Refused refused => Rejected(metrics, DisclosureRejection.RenderRefused, refused.Reason),
                RenderOutcome.Malformed malformed => Rejected(metrics, DisclosureRejection.MalformedReport, malformed.Reason),
                _ => null
            };
        }

        private static (byte[]? Pdf, string? Reason) Rendering(RenderOutcome outcome)
        {
            return outcome switch
            {
                RenderOutcome.Rendered rendered => (rendered.Pdf, null),
                RenderOutcome.Unrenderable unrenderable => (null, unrenderable.Reason),
                _ => (null, UnsupportedEngine)
            };
        }

        [LoggerMessage(
            2401,
            LogLevel.Information,
            "Disclosure {Id} published for {Organisation}, {PeriodFrom} to {PeriodTo}, rendered: {Rendered}.")]
        private static partial void LogPublished(
            ILogger logger, string id, string organisation, string periodFrom, string periodTo, bool rendered);
    }
}

/// <summary>One publication at a time: each runs the engine, and two would race on the same uuid.</summary>
public sealed class DisclosureImportGate() : RequestGate(1);

/// <summary>Anonymous share downloads at once: each holds a file of up to the PDF's size in memory.</summary>
public sealed class ShareGate() : RequestGate(4);
