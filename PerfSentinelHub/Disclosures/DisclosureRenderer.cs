using System.Text.Json;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Disclosures;

/// <summary>What disclose-pdf made of a disclosure, from its exit code and its one reason line.</summary>
public abstract record RenderOutcome
{
    /// <summary>Exit 0: rendered, so the content hash recomputed and the attestation names it.</summary>
    public sealed record Rendered(byte[] Pdf) : RenderOutcome;

    /// <summary>Exit 1: the document is not what it claims. Nothing is stored.</summary>
    public sealed record Refused(string Reason) : RenderOutcome;

    /// <summary>Exit 3, or anything outside the contract: not a report the Hub can take.</summary>
    public sealed record Malformed(string Reason) : RenderOutcome;

    /// <summary>Exit 5, or the Hub's own trouble: stored without a PDF, rendered again later.</summary>
    public sealed record Unrenderable(string Reason) : RenderOutcome;

    /// <summary>An engine without disclose-pdf: stored without a PDF, "not checked here".</summary>
    public sealed record Unsupported : RenderOutcome;
}

/// <summary>
///     Runs the embedded engine's disclose-pdf on a disclosure's files, in a private directory
///     deleted afterwards. Reads the exit code and the reason line only: the engine's stderr
///     never reaches a response.
/// </summary>
public sealed class DisclosureRenderer(EngineProbe engine, IOptions<HubOptions> options)
{
    public const string EngineContract = "engine_contract";
    private const int MaxReasonLineBytes = 4096;
    private const long MaxPdfBytes = 32L * 1024 * 1024;

    private static readonly Dictionary<int, string[]> Reasons = new()
    {
        [1] = ["content_hash_mismatch", "binding_mismatch"],
        [3] = ["missing_file", "file_too_large", "malformed_report", "output_unwritable"],
        [5] = ["unsupported_schema_version", "unsupported_characters", "pdf_export_failed"]
    };

    // Inside the import's 60-second budget, with room for the reads and the store.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(45);

    public async Task<RenderOutcome> RenderAsync(DisclosureFiles files, CancellationToken cancellationToken)
    {
        var analysis = options.Value.Analysis;
        if (analysis.EngineBinaryPath is not { } binary || !engine.SupportsDisclosePdf)
            return new RenderOutcome.Unsupported();

        var directory = Directory.CreateTempSubdirectory("perf-sentinel-hub-disclosure-");
        try
        {
            var output = Path.Combine(directory.FullName, "report.pdf");
            var arguments = await WriteInputsAsync(directory.FullName, files, cancellationToken);
            arguments.AddRange(["--output", output]);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            EngineResult result;
            try
            {
                result = await EngineProcess.RunAsync(
                    binary, arguments, MaxReasonLineBytes, analysis.ReportDirectory, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new RenderOutcome.Unrenderable("timeout");
            }
            catch (EngineOutputTooLargeException)
            {
                return new RenderOutcome.Malformed(EngineContract);
            }

            return await OutcomeAsync(result, output, cancellationToken);
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

    private async Task<List<string>> WriteInputsAsync(string directory, DisclosureFiles files,
        CancellationToken cancellationToken)
    {
        var report = Path.Combine(directory, "report.json");
        await File.WriteAllBytesAsync(report, files.Report, cancellationToken);
        var arguments = new List<string> { "disclose-pdf", "--report", report };
        if (files.Attestation is { } attestation)
        {
            var path = Path.Combine(directory, "attestation.intoto.jsonl");
            await File.WriteAllBytesAsync(path, attestation, cancellationToken);
            arguments.AddRange(["--attestation", path]);
        }

        if (files.Bundle is { } bundle)
        {
            var path = Path.Combine(directory, "bundle.sig");
            await File.WriteAllBytesAsync(path, bundle, cancellationToken);
            arguments.AddRange(["--bundle", path]);
        }

        if (options.Value.Disclosures is { ExpectedIdentity: { } identity, ExpectedIssuer: { } issuer })
            arguments.AddRange(["--expected-identity", identity, "--expected-issuer", issuer]);
        return arguments;
    }

    private static async Task<RenderOutcome> OutcomeAsync(EngineResult result, string output,
        CancellationToken cancellationToken)
    {
        if (result.ExitCode == 0)
            return File.Exists(output) && new FileInfo(output).Length is > 0 and <= MaxPdfBytes
                ? new RenderOutcome.Rendered(await File.ReadAllBytesAsync(output, cancellationToken))
                : new RenderOutcome.Malformed(EngineContract);

        if (!Reasons.TryGetValue(result.ExitCode, out var known) || ReasonOf(result.StandardOutput) is not { } reason ||
            !known.Contains(reason))
            return new RenderOutcome.Malformed(EngineContract);
        return result.ExitCode switch
        {
            1 => new RenderOutcome.Refused(reason),
            // The Hub could not write its own directory: nothing is wrong with what was sent.
            3 when reason == "output_unwritable" => new RenderOutcome.Unrenderable(reason),
            3 => new RenderOutcome.Malformed(reason),
            _ => new RenderOutcome.Unrenderable(reason)
        };
    }

    private static string? ReasonOf(byte[] standardOutput)
    {
        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("reason", out var reason) &&
                   reason.ValueKind == JsonValueKind.String
                ? reason.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
