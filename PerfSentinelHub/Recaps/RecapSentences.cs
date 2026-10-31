using PerfSentinelHub.Localization;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     The fixed plain-language sentence per finding type that the management view carries, from the
///     <c>recap.plain.*</c> texts the launcher reads too.
/// </summary>
public static class RecapSentences
{
    // A one-argument overload, not a default, so For still reads as a Func<string, string>.
    public static string For(string findingType)
    {
        return For(findingType, HubLang.En);
    }

    public static string For(string findingType, string lang)
    {
        return HubText.Get(lang, Knows(findingType) ? "recap.plain." + findingType : "recap.plain.other");
    }

    /// <summary>Whether the type has a sentence of its own, rather than the fallback.</summary>
    public static bool Knows(string findingType)
    {
        return findingType != "other" && HubText.Has("recap.plain." + findingType);
    }
}
