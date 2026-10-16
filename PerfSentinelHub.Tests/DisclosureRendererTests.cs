using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Disclosures;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The engine's disclose-pdf contract as the Hub reads it: the exit code and the one
///     reason line, never stderr, with the files in a private directory gone afterwards.
/// </summary>
public sealed class DisclosureRendererTests : IDisposable
{
    internal const string Report = """
        {"schema_version":"perf-sentinel-report/v1.7",
         "report_metadata":{"intent":"official","confidentiality_level":"public","integrity_level":"signed",
           "generated_at":"2026-10-05T12:00:00Z","report_uuid":"00000000-0000-4000-8000-000000000002"},
         "organisation":{"name":"Example SAS"},
         "period":{"from_date":"2026-07-01","to_date":"2026-09-30","period_type":"calendar-quarter"},
         "scope_manifest":{"total_applications_declared":4,"applications_measured":3},
         "aggregate":{"total_energy_kwh":18.42,"total_carbon_kgco2eq":1.91,"aggregate_waste_ratio":0.2,
           "canonical_waste":{"waste_ratio":0.18},"period_coverage":0.91},
         "applications":[{"service_name":"checkout","display_name":"Checkout","energy_kwh":9.4},
           {"service_name":"billing","energy_kwh":2.1}],
         "integrity":{"content_hash":"sha256:aaaa"}}
        """;

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-render-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task A_rendered_pdf_comes_back_and_the_private_directory_is_gone()
    {
        var renderer = await RendererAsync(new FakeEngine { DisclosePdf = true, DisclosePdfOutput = """{"result":"rendered"}""" },
            "https://github.com/acme/infra/.github/workflows/disclose.yml@refs/heads/main",
            "https://token.actions.githubusercontent.com");

        var outcome = await renderer.RenderAsync(Files(), TestContext.Current.CancellationToken);

        var rendered = Assert.IsType<RenderOutcome.Rendered>(outcome);
        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(rendered.Pdf));
        var arguments = await File.ReadAllTextAsync(Path.Combine(_workspace, "reports", "disclose-args.txt"),
            TestContext.Current.CancellationToken);
        Assert.Contains("--attestation", arguments, StringComparison.Ordinal);
        Assert.Contains("--expected-identity https://github.com/acme/infra", arguments, StringComparison.Ordinal);
        Assert.Contains("--expected-issuer https://token.actions.githubusercontent.com", arguments, StringComparison.Ordinal);
        AssertPrivateDirectoryGone(arguments);
    }

    [Theory]
    [InlineData(1, "content_hash_mismatch", "Refused")]
    [InlineData(1, "binding_mismatch", "Refused")]
    [InlineData(3, "malformed_report", "Malformed")]
    [InlineData(3, "file_too_large", "Malformed")]
    [InlineData(3, "missing_file", "Malformed")]
    // The Hub's own directory, not the publisher's file: stored without a PDF, rendered again later.
    [InlineData(3, "output_unwritable", "Unrenderable")]
    [InlineData(5, "unsupported_schema_version", "Unrenderable")]
    [InlineData(5, "unsupported_characters", "Unrenderable")]
    [InlineData(5, "pdf_export_failed", "Unrenderable")]
    public async Task Each_exit_code_and_reason_maps_to_its_outcome(int exitCode, string reason, string kind)
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            DisclosePdf = true,
            DisclosePdfExitCode = exitCode,
            DisclosePdfOutput = $$"""{"result":"refused","reason":"{{reason}}"}"""
        });

        var outcome = await renderer.RenderAsync(Files(), TestContext.Current.CancellationToken);

        Assert.Equal(kind, outcome.GetType().Name);
        Assert.Equal(reason, Reason(outcome));
        AssertPrivateDirectoryGone(await File.ReadAllTextAsync(
            Path.Combine(_workspace, "reports", "disclose-args.txt"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(1, """{"result":"refused","reason":"something_new"}""")]
    [InlineData(5, "not json")]
    [InlineData(3, "")]
    // clap's own usage error prints no reason line at all.
    [InlineData(2, "")]
    [InlineData(0, """{"result":"rendered"}""")]
    public async Task Anything_outside_the_contract_is_an_engine_contract_error(int exitCode, string output)
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            DisclosePdf = true,
            DisclosePdfExitCode = exitCode,
            DisclosePdfOutput = output,
            // Exit 0 without a PDF breaks the contract as surely as an unknown reason.
            DisclosePdfWritesNoPdf = exitCode == 0
        });

        var outcome = await renderer.RenderAsync(Files(), TestContext.Current.CancellationToken);

        Assert.Equal("engine_contract", Assert.IsType<RenderOutcome.Malformed>(outcome).Reason);
    }

    [Fact]
    public async Task An_engine_without_disclose_pdf_is_unsupported_and_never_run()
    {
        var renderer = await RendererAsync(new FakeEngine { DisclosePdf = false });

        Assert.IsType<RenderOutcome.Unsupported>(await renderer.RenderAsync(Files(), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "disclose-args.txt")));
    }

    [Fact]
    public async Task A_render_past_its_deadline_is_unrenderable_timeout()
    {
        var renderer = await RendererAsync(new FakeEngine { DisclosePdf = true, SleepSeconds = 5 });
        renderer.Timeout = TimeSpan.FromMilliseconds(300);

        var outcome = await renderer.RenderAsync(Files(), TestContext.Current.CancellationToken);

        Assert.Equal("timeout", Assert.IsType<RenderOutcome.Unrenderable>(outcome).Reason);
    }

    [Fact]
    public void The_envelope_reads_what_the_hub_files_and_the_card_figures()
    {
        Assert.True(DisclosureEnvelope.TryParse(Encoding.UTF8.GetBytes(Report), out var envelope));

        Assert.Equal("00000000-0000-4000-8000-000000000002", envelope.ReportUuid);
        Assert.Equal("Example SAS", envelope.Organisation);
        Assert.Equal(("calendar-quarter", "2026-07-01", "2026-09-30"), (envelope.PeriodType, envelope.PeriodFrom, envelope.PeriodTo));
        Assert.Equal(("official", "public", "signed"), (envelope.Intent, envelope.Confidentiality, envelope.IntegrityLevel));
        Assert.Equal("sha256:aaaa", envelope.ContentHash);
        Assert.False(envelope.Internal);
        Assert.Equal(
            """{"energy_kwh":18.42,"carbon_kgco2eq":1.91,"avoidable_ratio":0.18,"coverage":0.91,"applications_declared":4,"applications_measured":3,"applications":[{"name":"Checkout","energy_kwh":9.4},{"name":"billing","energy_kwh":2.1}]}""",
            envelope.FiguresJson);
    }

    [Fact]
    public void The_envelope_reads_an_internal_report_and_refuses_one_without_a_uuid()
    {
        var internalReport = Report.Replace("\"public\"", "\"internal\"", StringComparison.Ordinal);
        Assert.True(DisclosureEnvelope.TryParse(Encoding.UTF8.GetBytes(internalReport), out var envelope));
        Assert.True(envelope.Internal);

        var noUuid = Report.Replace("\"report_uuid\"", "\"other\"", StringComparison.Ordinal);
        Assert.False(DisclosureEnvelope.TryParse(Encoding.UTF8.GetBytes(noUuid), out _));
        Assert.False(DisclosureEnvelope.TryParse("not json"u8.ToArray(), out _));
        Assert.False(DisclosureEnvelope.TryParse("[]"u8.ToArray(), out _));
    }

    [Theory]
    [InlineData("\"public\"", "\"INTERNAL\"")]
    [InlineData("\"public\"", "\"restricted\"")]
    [InlineData("\"2026-07-01\"", "\"soon\"")]
    [InlineData("\"2026-09-30\"", "\"2026-13-01\"")]
    public void The_envelope_refuses_a_confidentiality_or_a_date_the_schema_does_not_have(string valid, string invalid)
    {
        var report = Report.Replace(valid, invalid, StringComparison.Ordinal);

        Assert.False(DisclosureEnvelope.TryParse(Encoding.UTF8.GetBytes(report), out _));
    }

    private static string? Reason(RenderOutcome outcome)
    {
        return outcome switch
        {
            RenderOutcome.Refused refused => refused.Reason,
            RenderOutcome.Malformed malformed => malformed.Reason,
            RenderOutcome.Unrenderable unrenderable => unrenderable.Reason,
            _ => null
        };
    }

    private static void AssertPrivateDirectoryGone(string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var output = parts[Array.IndexOf(parts, "--output") + 1];
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)), "the private directory must be deleted");
    }

    private static DisclosureFiles Files()
    {
        return new DisclosureFiles(Encoding.UTF8.GetBytes(Report), "attestation"u8.ToArray(), null);
    }

    private async Task<DisclosureRenderer> RendererAsync(FakeEngine engine, string? identity = null, string? issuer = null)
    {
        var options = Options.Create(new HubOptions
        {
            Analysis = new AnalysisOptions
            {
                EngineBinaryPath = (engine with { Version = "perf-sentinel 0.31.0" }).WriteTo(Path.Combine(_workspace, "perf-sentinel")),
                ReportDirectory = Path.Combine(_workspace, "reports")
            },
            Disclosures = new DisclosuresOptions { ExpectedIdentity = identity, ExpectedIssuer = issuer }
        });
        var probe = new EngineProbe(options, NullLogger<EngineProbe>.Instance);
        await probe.StartAsync(TestContext.Current.CancellationToken);
        return new DisclosureRenderer(probe, options);
    }
}
