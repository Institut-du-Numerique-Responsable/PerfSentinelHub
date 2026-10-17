namespace PerfSentinelHub.Api;

/// <summary>Terminal sign-in's fixed values: deliberately not settings.</summary>
public static class TerminalAccess
{
    public const string ClientId = "perf-sentinel";
    public const string DeviceCodeGrant = "urn:ietf:params:oauth:grant-type:device_code";
    public const string TokenPrefix = "pshub_";

    // No vowel, so a code never spells a word, and no letter that reads as a digit.
    public const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";
    public const int UserCodeLength = 8;
    public const int ExpiresInSeconds = 600;
    public const int IntervalSeconds = 5;
    public const int MaxPending = 200;
    public const int MaxPendingPerAddress = 10;
    public const int MaxFailures = 10;
    public const long FailureWindowMs = 600_000;
    public const long MinTokenLifetimeMs = 3_600_000;
    public const long LastUseResolutionMs = 300_000;
    public const int MaxTokensPerSubject = 10;
}
