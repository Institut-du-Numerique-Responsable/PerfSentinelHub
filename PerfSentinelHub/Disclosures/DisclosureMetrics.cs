namespace PerfSentinelHub.Disclosures;

/// <summary>Why a publication was refused. A closed set: these become Prometheus label values.</summary>
public enum DisclosureRejection
{
    Unauthorized,
    GateFull,
    TooLarge,
    UnsupportedMediaType,

    /// <summary>A body, a part or a report the Hub or the engine cannot read.</summary>
    MalformedReport,

    /// <summary>The engine refused it: the hash does not recompute or the attestation names another.</summary>
    RenderRefused,
    InternalRefused,

    /// <summary>Another report under a uuid already published.</summary>
    Conflict
}

/// <summary>
///     Refused publications and failed webhook deliveries, in process like the import
///     counters. Published only while disclosures are on, so a Hub without them scrapes
///     as it did before.
/// </summary>
public sealed class DisclosureMetrics
{
    private static readonly DisclosureRejection[] Reasons = Enum.GetValues<DisclosureRejection>();
    private readonly long[] _counts = new long[Reasons.Length];
    private long _deliveriesFailed;

    public long DeliveriesFailed => Interlocked.Read(ref _deliveriesFailed);

    public T Refuse<T>(DisclosureRejection reason, T result)
    {
        Interlocked.Increment(ref _counts[(int)reason]);
        return result;
    }

    public void DeliveryFailed()
    {
        Interlocked.Increment(ref _deliveriesFailed);
    }

    public IEnumerable<(string Reason, long Count)> Snapshot()
    {
        foreach (var reason in Reasons)
            yield return (Label(reason), Interlocked.Read(ref _counts[(int)reason]));
    }

    private static string Label(DisclosureRejection reason)
    {
        return reason switch
        {
            DisclosureRejection.Unauthorized => "unauthorized",
            DisclosureRejection.GateFull => "gate_full",
            DisclosureRejection.TooLarge => "too_large",
            DisclosureRejection.UnsupportedMediaType => "unsupported_media_type",
            DisclosureRejection.MalformedReport => "malformed_report",
            DisclosureRejection.RenderRefused => "render_refused",
            DisclosureRejection.InternalRefused => "internal_refused",
            DisclosureRejection.Conflict => "conflict",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
        };
    }
}
