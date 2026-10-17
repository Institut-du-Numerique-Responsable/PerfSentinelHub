namespace PerfSentinelHub.Disclosures;

/// <summary>What disclose-pdf made of a disclosure, from its exit code and its one reason line.</summary>
public abstract record RenderOutcome
{
    /// <summary>Exit 0: rendered, so the content hash recomputed and the attestation names it.</summary>
    public sealed record Rendered(byte[] Pdf) : RenderOutcome;

    /// <summary>Exit 1: the document is not what it claims. Nothing is stored.</summary>
    public sealed record Refused(string Reason) : RenderOutcome;

    /// <summary>Exit 3, or anything outside the contract: not a report the Hub can take.</summary>
    public sealed record Malformed(string Reason) : RenderOutcome;

    /// <summary>Exit 5, or the Hub's own trouble: stored without a PDF, rendered again later.</summary>
    public sealed record Unrenderable(string Reason) : RenderOutcome;

    /// <summary>An engine without disclose-pdf: stored without a PDF, "not checked here".</summary>
    public sealed record Unsupported : RenderOutcome;
}
