using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PerfSentinelHub.Api;

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

    public bool IsNothing => Patterns is { Count: 0 };

    public static AccessScope Of(IReadOnlyList<AccessPattern> patterns)
    {
        return patterns.Count == 0 ? Nothing : new AccessScope(patterns);
    }

    // Some pattern whose source part is absent or matches: the source holds something in scope.
    public bool ReachesSource(string source)
    {
        return Patterns?.Any(pattern => pattern.MatchesSource(source)) ?? true;
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
