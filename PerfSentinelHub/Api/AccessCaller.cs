namespace PerfSentinelHub.Api;

/// <summary>
///     Who a decision is made for. The subject is the authorization key and nothing
///     else is compared; the groups are the provider groups frozen at sign-in.
///     A machine token's subject is <c>machine:&lt;name&gt;</c> and its groups come
///     from the configuration alone.
/// </summary>
public sealed record AccessCaller(string Kind, string Subject, IReadOnlyList<string> ProviderGroups)
{
    public const string Session = "session";
    public const string TerminalToken = "terminal_token";
    public const string MachineToken = "machine_token";
    public const string MachinePrefix = "machine:";
}
