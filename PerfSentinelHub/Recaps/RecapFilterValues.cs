namespace PerfSentinelHub.Recaps;

/// <summary>
///     The services or namespaces a recap is asked for. They are bounded, then sorted and made unique, so
///     the same choices in another order are one recap and one job.
/// </summary>
public static class RecapFilterValues
{
    public const int MaxValues = 50;
    public const int MaxChars = 256;

    // A control character is refused in a value, so no value can hold the separator.
    private const char KeySeparator = '\u001f';

    /// <summary>
    ///     The canonical list of <paramref name="given" />, null when it is empty. False when it holds more
    ///     than <see cref="MaxValues" />, or a value that is empty, past <see cref="MaxChars" /> or has a
    ///     control character.
    /// </summary>
    public static bool TryCanonical(IReadOnlyCollection<string> given, out IReadOnlyList<string>? values)
    {
        values = null;
        if (given.Count > MaxValues || given.Any(value => value.Length is 0 or > MaxChars || value.Any(char.IsControl)))
            return false;
        if (given.Count > 0)
            values = [.. given.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        return true;
    }

    /// <summary>The key of a canonical list: its values joined, null for none.</summary>
    public static string? KeyOf(IReadOnlyList<string>? values)
    {
        return values is null ? null : string.Join(KeySeparator, values);
    }
}
