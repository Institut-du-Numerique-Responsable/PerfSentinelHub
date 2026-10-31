using System.Globalization;

namespace PerfSentinelHub.Localization;

/// <summary>The two languages the Hub writes its human pages in, English by default.</summary>
public static class HubLang
{
    public const string En = "en";
    public const string Fr = "fr";

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // The groups by hand, as launcher.js writes them: ICU builds disagree on the space fr-FR uses.
    private static readonly CultureInfo French = FrenchCulture();

    /// <summary>The language a value names, English for anything else.</summary>
    public static string Normalize(string? value)
    {
        return string.Equals(value, Fr, StringComparison.OrdinalIgnoreCase) ? Fr : En;
    }

    /// <summary>True for the exact codes a caller may send, en or fr.</summary>
    public static bool IsKnown(string? value)
    {
        return value is En or Fr;
    }

    public static CultureInfo Culture(string lang)
    {
        return lang == Fr ? French : English;
    }

    /// <summary>The plural form of <paramref name="count" />: English says one for 1 alone, French for 0 and 1.</summary>
    public static string Plural(string lang, long count)
    {
        return (lang == Fr ? count <= 1 : count == 1) ? "one" : "other";
    }

    private static CultureInfo FrenchCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
        culture.NumberFormat.NumberGroupSeparator = " ";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return CultureInfo.ReadOnly(culture);
    }
}
