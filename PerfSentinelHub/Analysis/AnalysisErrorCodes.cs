namespace PerfSentinelHub.Analysis;

/// <summary>
///     The bounded vocabulary a run may report. Raw stderr never leaves the
///     process, so this is all the operator gets, and every code needs one
///     actionable sentence in the interface.
/// </summary>
public static class AnalysisErrorCodes
{
    public const string SourceUnreachable = "source_unreachable";
    public const string SourceAuthFailed = "source_auth_failed";
    public const string SourceRejectedRequest = "source_rejected_request";
    public const string Timeout = "timeout";
    public const string OutputTooLarge = "output_too_large";
    public const string BinaryFailed = "binary_failed";
    public const string InvalidRequest = "invalid_request";
    public const string HubMemoryLow = "hub_memory_low";
    public const string Internal = "internal";
}
