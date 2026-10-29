using System.Text.Json;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Api;

/// <summary>
///     What a caller asks the Hub to record on a finding: a new action, by status or by taking
///     it, or a change of an existing one. <c>OwnerSet</c> tells a cleared owner from an absent one.
/// </summary>
public sealed record ActionWrite(string Signature, string? Status, string? Owner, bool OwnerSet, bool Take)
{
    private const int MaxOwnerChars = 256;

    public static ActionWrite? TryParse(ReadOnlyMemory<byte> payload, bool create, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryRead(document.RootElement, create, out error);
        }
        // The second is an unpaired surrogate, a string the reader cannot hand over.
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Refuse("The body is not valid JSON.", out error);
        }
    }

    private static ActionWrite? TryRead(JsonElement root, bool create, out string? error)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Refuse("The body must be a JSON object.", out error);
        if (AckWrite.ReadText(root, "signature", AckParser.MaxSignatureLength) is not { } signature ||
            signature is "." or "..")
            return Refuse("signature is required, 1024 characters at most and no control character.", out error);
        if (!TryReadStatus(root, out var status))
            return Refuse("status must be todo, in_progress or done.", out error);
        if (!TryReadOwner(root, create, out var owner, out var ownerSet))
            return Refuse(
                create
                    ? "owner must be 1 to 256 characters with no control character."
                    : "owner must be 1 to 256 characters with no control character, or null to clear it.",
                out error);
        if (!TryReadTake(root, out var take))
            return Refuse("take must be true or false.", out error);

        error = Judge(create, status, ownerSet, take);
        return error is null ? new ActionWrite(signature, status, owner, ownerSet, take) : null;
    }

    // What the fields mean together, once each is well formed.
    private static string? Judge(bool create, string? status, bool ownerSet, bool take)
    {
        if (take && !create)
            return "take applies to a new action only.";
        if (take && (status is not null || ownerSet))
            return "take cannot be combined with status or owner.";
        if (create && !take && status is null)
            return "An action needs a status or take.";
        return !create && status is null && !ownerSet ? "A change needs a status or an owner." : null;
    }

    private static bool TryReadStatus(JsonElement root, out string? status)
    {
        status = null;
        if (!root.TryGetProperty("status", out var value))
            return true;
        status = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return status is not null && ActionStatuses.All.Contains(status, StringComparer.Ordinal);
    }

    // A null owner clears it, which only a change of an existing action can mean.
    private static bool TryReadOwner(JsonElement root, bool create, out string? owner, out bool ownerSet)
    {
        owner = null;
        ownerSet = root.TryGetProperty("owner", out var value);
        if (!ownerSet)
            return true;
        if (value.ValueKind == JsonValueKind.Null)
            return !create;
        owner = AckWrite.ReadText(root, "owner", MaxOwnerChars);
        return owner is not null;
    }

    private static bool TryReadTake(JsonElement root, out bool take)
    {
        take = false;
        if (!root.TryGetProperty("take", out var value))
            return true;
        take = value.ValueKind == JsonValueKind.True;
        return take || value.ValueKind == JsonValueKind.False;
    }

    private static ActionWrite? Refuse(string why, out string? error)
    {
        error = why;
        return null;
    }
}
