namespace PerfSentinelHub.Cards;

public enum CardRevokeOutcome
{
    Revoked,
    Unknown,
    BadDate
}

/// <summary>The engine could not serve a card request: <c>unsupported</c>, <c>timeout</c> or <c>engine_contract</c>.</summary>
public sealed class CardEngineException(string reason) : Exception($"The engine could not serve the card request: {reason}.")
{
    public string Reason { get; } = reason;
}

/// <summary>One signing key as the administrator publishes it: dates as UTC days, the DNS line ready to paste.</summary>
public sealed record CardKeyItem(
    string Fingerprint,
    string PublicKey,
    string From,
    string? To,
    string? Revoked,
    bool Current,
    string Dns);

/// <summary>Every key, oldest first, with the record name (null without a domain) and the .well-known file.</summary>
public sealed record CardKeysResponse(string? RecordName, IReadOnlyList<CardKeyItem> Keys, string WellKnown);
