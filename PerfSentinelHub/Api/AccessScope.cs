using System.Buffers;
using System.Text;
using System.Text.Json;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

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
        return part is null ? (null, false) : part.EndsWith('*') ? (part[..^1], true) : (part, false);
    }
}

/// <summary>
///     What one caller may reach with one permission: everything, nothing, or the union of
///     some patterns. Unrestricted keeps today's SQL; patterns travel to SQL as one bound
///     parameter, $access_patterns, built here and never interpolated.
/// </summary>
public sealed class AccessScope
{
    public static readonly AccessScope Unrestricted = new(null);
    public static readonly AccessScope Nothing = new([]);

    private AccessScope(IReadOnlyList<AccessPattern>? patterns)
    {
        Patterns = patterns;
    }

    // Null when unrestricted, empty for nothing.
    public IReadOnlyList<AccessPattern>? Patterns { get; }

    public static AccessScope Of(IReadOnlyList<AccessPattern> patterns)
    {
        return patterns.Count == 0 ? Nothing : new AccessScope(patterns);
    }

    public bool Reads(AccessCopy copy)
    {
        return Patterns?.Any(pattern => pattern.Reads(copy)) ?? true;
    }

    public bool Writes(AccessCopy copy)
    {
        return Patterns?.Any(pattern => pattern.Writes(copy)) ?? true;
    }

    public bool CoversSubject(string source, string service, IReadOnlyList<AccessCopy> copies)
    {
        return Patterns?.Any(pattern => pattern.CoversSubject(source, service, copies)) ?? true;
    }

    // At a source as a whole: '*', or a pattern that sets Source alone and matches it.
    public bool HoldsAtSource(string source)
    {
        return Patterns?.Any(pattern => pattern.IsWholeSource && pattern.MatchesSource(source)) ?? true;
    }

    // The $access_patterns array HubDatabase.AccessPatternsCte decodes.
    public string ToJson()
    {
        var patterns = Patterns ?? throw new InvalidOperationException("An unrestricted scope keeps today's SQL.");
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var pattern in patterns)
            {
                writer.WriteStartObject();
                writer.WriteString("source", pattern.Source);
                writer.WriteNumber("source_prefix", pattern.SourceIsPrefix ? 1 : 0);
                writer.WriteString("service", pattern.Service);
                writer.WriteNumber("service_prefix", pattern.ServiceIsPrefix ? 1 : 0);
                writer.WriteString("key", pattern.Key);
                writer.WriteString("value", pattern.Value);
                writer.WriteNumber("value_prefix", pattern.ValueIsPrefix ? 1 : 0);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
