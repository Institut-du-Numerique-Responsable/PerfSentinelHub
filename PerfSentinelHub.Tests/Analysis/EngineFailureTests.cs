using PerfSentinelHub.Analysis;

namespace PerfSentinelHub.Tests.Analysis;

public sealed class EngineFailureTests
{
    [Theory]
    [InlineData("2026-11-06T22:45:15Z ERROR sentinel_core::ingest::tempo: fetch HTTP 401 total=10\nError fetching traces from Tempo: the search matched 10 trace(s) but most fetches failed. If Tempo is running out of memory, lower --fetch-concurrency", AnalysisErrorCodes.FetchesFailed)]
    [InlineData("Error fetching traces from Tempo: the search matched 10 trace(s) but every fetch failed", AnalysisErrorCodes.FetchesFailed)]
    [InlineData("\u001b[2m2026-11-06T22:45:15Z\u001b[0m \u001b[32m INFO\u001b[0m fetching count=400\nError: Tempo returned HTTP 400 for https://tempo/api/search", AnalysisErrorCodes.SourceRejectedRequest)]
    // The 400 in the log line is a count. The 503 is the backend down behind an answering ingress, as for a daemon.
    [InlineData("2026-11-06T22:45:15Z  INFO sentinel_core: max_traces=400\nError: backend returned HTTP 503 for https://tempo/api/traces", AnalysisErrorCodes.SourceUnreachable)]
    [InlineData("Error: Tempo returned HTTP 404 for https://tempo/api/search", AnalysisErrorCodes.SourceRejectedRequest)]
    [InlineData("memory allocation of 38502400 bytes failed\nnote: run with `RUST_BACKTRACE=1` environment variable to display a backtrace", AnalysisErrorCodes.EngineOutOfMemory)]
    // The cap refusing a thread its stack: the Rust runtime panics on the spawn rather than on an allocation.
    [InlineData("thread 'main' panicked at /rustc/x/library/std/src/thread/mod.rs:1:1:\nfailed to spawn thread: Os { code: 11, kind: WouldBlock, message: \"Resource temporarily unavailable\" }", AnalysisErrorCodes.EngineOutOfMemory)]
    [InlineData("thread 'main' panicked: OS can't spawn worker thread: Os { code: 12, kind: OutOfMemory, message: \"Cannot allocate memory\" }", AnalysisErrorCodes.EngineOutOfMemory)]
    [InlineData("Error: backend returned HTTP 403 for https://tempo/api/search", AnalysisErrorCodes.SourceAuthFailed)]
    [InlineData("Error fetching traces from Tempo: request timed out", AnalysisErrorCodes.Timeout)]
    // Engine 0.43.2: a refused connection and a DNS failure both print this.
    [InlineData("Error fetching traces from Tempo: HTTP transport error: client error (Connect)", AnalysisErrorCodes.SourceUnreachable)]
    [InlineData("Error fetching traces from Jaeger query API: HTTP transport error: client error (Connect)", AnalysisErrorCodes.SourceUnreachable)]
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
