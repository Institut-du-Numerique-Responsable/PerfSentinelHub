namespace PerfSentinelHub.Access;

/// <summary>
///     What a browser session approved a code with: the owner of the token to come. Groups are
///     the session's filtered provider groups, RequiredGroup the step-1 group that admitted it.
/// </summary>
public sealed record DeviceApproval(
    string Subject,
    string Identity,
    IReadOnlyList<string> ProviderGroups,
    string? RequiredGroup,
    DateTimeOffset ExpiresUtc,
    bool OtherNetwork);

public sealed record DeviceStart(string DeviceCode, string UserCode);

// A pending code as the approval page shows it.
public sealed record PendingCode(string UserCode, string ClientAddress, long RequestedAtMs);

// Error is an RFC 8628 code, or null with an approval the caller now holds alone.
public sealed record DevicePoll(string? Error, DeviceApproval? Approval = null, string? ClientAddress = null);
