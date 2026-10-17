using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Access;

/// <summary>
///     One finding_attributes row: a captured pair under the grouping identity of the
///     observation it came with. ('', '', '', '') is H1's "no grouping" marker, and an
///     incident pair carries no identity.
/// </summary>
public sealed record AccessAttribute(string GroupingKey, string GroupingValue, string Key, string Value);

/// <summary>A finding copy (signature, source) or an incident copy (id, source): what a pattern matches.</summary>
public sealed record AccessCopy(string Source, string? Service, IReadOnlyList<AccessAttribute> Rows);

/// <summary>
///     A project pattern compiled once at startup. Each part is null, exact, or a prefix
///     whose stem is held without its '*'. Comparisons are ordinal, and SQL uses
///     substr rather than LIKE or GLOB for the same reason.
/// </summary>
public sealed record AccessPattern(
    string? Source,
    bool SourceIsPrefix,
    string? Service,
    bool ServiceIsPrefix,
    string? Key,
    string? Value,
    bool ValueIsPrefix)
{
    public bool IsWholeSource => Source is not null && Service is null && Key is null;

    // Takes a pattern HubOptionsValidator accepted.
    public static AccessPattern Compile(PatternOptions pattern)
    {
        var (source, sourceIsPrefix) = Part(pattern.Source);
        var (service, serviceIsPrefix) = Part(pattern.Service);
        string? key = null;
        (string? Stem, bool IsPrefix) value = (null, false);
        // Inverted, the constructor call would be written twice.
        // ReSharper disable once InvertIf
        if (pattern.Attribute is { } attribute)
        {
            var equals = attribute.IndexOf('=', StringComparison.Ordinal);
            key = attribute[..equals];
            value = Part(attribute[(equals + 1)..]);
        }

        return new AccessPattern(source, sourceIsPrefix, service, serviceIsPrefix, key, value.Stem, value.IsPrefix);
    }

    public bool MatchesSource(string source)
    {
        return Source is null || M(Source, SourceIsPrefix, source);
    }

    // Read (any): some row carries the pair, whichever grouping it came with.
    public bool Reads(AccessCopy copy)
    {
        return MatchesSource(copy.Source) && MatchesService(copy.Service) &&
               (Key is null || copy.Rows.Any(row => row.Key == Key && M(Value!, ValueIsPrefix, row.Value)));
    }

    // Write (all), for acks.write and analyses.run.
    public bool Writes(AccessCopy copy)
    {
        return MatchesSource(copy.Source) && MatchesService(copy.Service) && (Key is null || AttributeWrites(copy));
    }

    // A trace-backend run on service X at backend T, judged on the Hub's copies of X in T's
    // environment: the engine exports no grouping for a span, so they are the only evidence.
    // The caller applies that bound and passes only those copies.
    public bool CoversSubject(string source, string service, IReadOnlyList<AccessCopy> copies)
    {
        return MatchesSource(source) && MatchesService(service) &&
               (Key is null || (copies.Count > 0 && copies.All(AttributeWrites)));
    }

    private bool MatchesService(string? service)
    {
        return Service is null || M(Service, ServiceIsPrefix, service);
    }

    // At least one row; every grouping identity, the marker's included, carries the key; and
    // every value of the key matches. One planted grouping is then enough to refuse.
    private bool AttributeWrites(AccessCopy copy)
    {
        return copy.Rows.Count > 0 &&
               copy.Rows.GroupBy(row => (row.GroupingKey, row.GroupingValue)).All(identity =>
                   identity.Any(row => row.Key == Key)) &&
               copy.Rows.Where(row => row.Key == Key).All(row => M(Value!, ValueIsPrefix, row.Value));
    }

    // A NULL value, a copy with no service of its own, matches no part.
    private static bool M(string stem, bool isPrefix, string? value)
    {
        return value is not null && (isPrefix ? value.StartsWith(stem, StringComparison.Ordinal) : value == stem);
    }

    private static (string? Stem, bool IsPrefix) Part(string? part)
    {
        if (part is null) return (null, false);
        return part.EndsWith('*') ? (part[..^1], true) : (part, false);
    }
}
