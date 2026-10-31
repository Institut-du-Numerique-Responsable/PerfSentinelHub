using System.Globalization;

namespace PerfSentinelHub.Localization;

/// <summary>The two languages the Hub writes its human pages in, English by default.</summary>
public static class HubLang
{
    public const string En = "en";
    public const string Fr = "fr";

    /// <summary>The launcher's language button writes its choice here too, for the pages a redirect reaches.</summary>
    public const string Cookie = "perf-sentinel-lang";

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

    /// <summary>
    ///     The language of a server page: <paramref name="lang" /> when it is en or fr, else the launcher's
    ///     choice cookie, else the first en or fr variant of Accept-Language by quality, else English.
    /// </summary>
    public static string Of(HttpRequest request, string? lang)
    {
        if (IsKnown(lang)) return lang!;
        if (request.Cookies[Cookie] is { } chosen && IsKnown(chosen)) return chosen;
        foreach (var range in request.GetTypedHeaders().AcceptLanguage.Where(range => range.Quality is not 0)
                     .OrderByDescending(range => range.Quality ?? 1))
        {
            var primary = range.Value.Value?.Split('-')[0].ToLowerInvariant();
            if (primary is En or Fr) return primary;
        }

        return En;
    }

    public static CultureInfo Culture(string lang)
    {
        return lang == Fr ? French : English;
    }

    /// <summary>The plural form of <paramref name="count" />: English says one for 1 alone, French for 0 and 1.</summary>
    public static string Plural(string lang, long count)
    {
        var one = lang == Fr ? count <= 1 : count == 1;
        return one ? "one" : "other";
    }

    private static CultureInfo FrenchCulture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
        culture.NumberFormat.NumberGroupSeparator = "\u202F";
        culture.NumberFormat.NumberDecimalSeparator = ",";
        return CultureInfo.ReadOnly(culture);
    }
}
