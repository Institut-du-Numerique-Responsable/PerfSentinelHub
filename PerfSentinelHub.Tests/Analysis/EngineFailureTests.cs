using PerfSentinelHub.Analysis;

namespace PerfSentinelHub.Tests.Analysis;

public sealed class EngineFailureTests
{
    [Theory]
    [InlineData("2026-11-06T22:45:15Z ERROR sentinel_core::ingest::tempo: fetch HTTP 401 total=10\nError fetching traces from Tempo: the search matched 10 trace(s) but most fetches failed. If Tempo is running out of memory, lower --fetch-concurrency", AnalysisErrorCodes.FetchesFailed)]
    [InlineData("Error fetching traces from Tempo: the search matched 10 trace(s) but every fetch failed", AnalysisErrorCodes.FetchesFailed)]
    [InlineData("\u001b[2m2026-11-06T22:45:15Z\u001b[0m \u001b[32m INFO\u001b[0m fetching count=400\nError: Tempo returned HTTP 400 for https://tempo/api/search", AnalysisErrorCodes.SourceRejectedRequest)]
    [InlineData("2026-11-06T22:45:15Z  INFO sentinel_core: max_traces=400\nError: backend returned HTTP 503 for https://tempo/api/traces", AnalysisErrorCodes.BinaryFailed)]
    [InlineData("memory allocation of 38502400 bytes failed\nnote: run with `RUST_BACKTRACE=1` environment variable to display a backtrace", AnalysisErrorCodes.EngineOutOfMemory)]
    [InlineData("Error: backend returned HTTP 403 for https://tempo/api/search", AnalysisErrorCodes.SourceAuthFailed)]
    [InlineData("Error fetching traces from Tempo: request timed out", AnalysisErrorCodes.Timeout)]
    [InlineData("", AnalysisErrorCodes.BinaryFailed)]
    public void The_code_comes_from_the_diagnostic_lines(string stderr, string expected)
    {
        Assert.Equal(expected, EngineFailure.Classify(stderr));
    }

    [Fact]
    public void Log_lines_and_colors_are_left_out_of_the_diagnostic()
    {
        Assert.Equal(
            "Error: x",
            EngineFailure.DiagnosticLines("\u001b[2m2026-11-06T22:45:15.5Z\u001b[0m ERROR a: HTTP 401\nError: x"));
    }
}
