using System.Text.RegularExpressions;

namespace PerfSentinelHub.Analysis;

/// <summary>
///     Names an owner from what a failed engine invocation printed. A heuristic on
///     purpose: the engine has no exit code per failure kind, and "the backend refused
///     us" and "the binary broke" have different owners and different next steps. It
///     reads the engine's diagnostic lines only: the log lines carry timestamps and
///     counters, and a bare <c>400</c> in them is a count, not a status.
/// </summary>
public static partial class EngineFailure
{
    // In the order they apply: the first rule with a match names the code.
    private static readonly (string[] Needles, string Code)[] Rules =
    [
        (["HTTP 401", "HTTP 403", "unauthorized", "forbidden"], AnalysisErrorCodes.SourceAuthFailed),
        (["memory allocation of"], AnalysisErrorCodes.EngineOutOfMemory),
        (["most fetches failed", "every fetch failed"], AnalysisErrorCodes.FetchesFailed),
        // A timed-out query is a window too wide for the engine's own request deadline,
        // not a backend that is down.
        (["timed out"], AnalysisErrorCodes.Timeout),
        // The engine's own body cap: the launcher's output_too_large gloss already says
        // exactly the right remedy.
        (["byte cap perf-sentinel applies"], AnalysisErrorCodes.OutputTooLarge),
        (["connection refused", "dns error", "error sending request"], AnalysisErrorCodes.SourceUnreachable),
        (["HTTP 400", "bad request"], AnalysisErrorCodes.SourceRejectedRequest)
    ];

    public static string Classify(string standardError)
    {
        var diagnostic = DiagnosticLines(standardError);
        foreach (var (needles, code) in Rules)
            if (needles.Any(needle => diagnostic.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return code;
        return AnalysisErrorCodes.BinaryFailed;
    }

    /// <summary>
    ///     The stderr without its colors (the engine colors even a piped stream) and
    ///     without its tracing lines, which start with an ISO timestamp and carry a level.
    /// </summary>
    public static string DiagnosticLines(string standardError)
    {
        var lines = AnsiSequence().Replace(standardError, "").Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !LogLine().IsMatch(line));
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiSequence();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}.* (INFO|WARN|ERROR|DEBUG|TRACE) ")]
    private static partial Regex LogLine();
}
