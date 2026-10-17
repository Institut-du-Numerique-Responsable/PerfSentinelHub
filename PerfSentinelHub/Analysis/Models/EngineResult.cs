namespace PerfSentinelHub.Analysis;

/// <summary>
///     Outcome of one engine invocation. <see cref="StandardError" /> is kept for
///     classification only and never reaches a response: ARCH 6.6 bounds what the
///     UI may show to a vocabulary of error codes.
/// </summary>
public sealed record EngineResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
