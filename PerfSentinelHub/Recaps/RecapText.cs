using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Localization;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     The recap page's copy, in C#. Each method returns exactly the string the launcher.js helper of the
///     same role returns for the same input (PSL.co2Text, trendText, ...), so a frozen report and the
///     #/recap screen cannot read differently: keep this class in step with launcher.js, and
///     RecapTextTests pins the examples both sides share. Numbers follow JavaScript's rounding, not
///     .NET's: Math.round is round-half-up, where Math.Round is banker's.
/// </summary>
public static class RecapText
{
    private static readonly string[] FrenchMonths =
        ["janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc."];

    private static string T(string lang, string key, params (string Name, string Value)[] values)
    {
        return HubText.Get(lang, key, values);
    }

    private static string Audience(string view)
    {
        return view == RecapViewNames.Pilotage ? "pilotage" : "tech";
    }

    /// <summary>A count with its groups, as PSL.num writes it.</summary>
    public static string Number(double value, string lang)
    {
        return value.ToString("#,0.###", HubLang.Culture(lang));
    }

    /// <summary>The carbon estimate in three pieces, as <c>co2Parts</c> returns them.</summary>
    public static (string Lead, string Figure, string Unit) Co2Parts(
        double? grams, int sourcesWithoutCoefficient, string? missingReason, string view = "tech",
        string lang = HubLang.En)
    {
        if (grams is null && view == RecapViewNames.Pilotage)
            return ("", "—", T(lang, "recap.co2.na"));
        if (grams is not { } value)
            return ("", "—",
                T(lang, missingReason == "no_avoidable_io" ? "recap.co2.no_avoidable_io" : "recap.co2.no_coefficient"));
        // A nonzero estimate never rounds to zero; "at least" would contradict "< 1 g".
        if (value is > 0 and < 1)
            return ("", "< 1 g", "CO2e");
        var kilos = value >= 1000;
        // toPrecision(3) on the kilos, Math.round on the grams.
        var shown = kilos
            ? double.Parse((value / 1000).ToString("E2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : Math.Floor(value + 0.5);
        return (sourcesWithoutCoefficient > 0 ? T(lang, "recap.co2.lead") : "",
            "~" + Number(shown, lang) + (kilos ? " kg" : " g"), "CO2e");
    }

    public static string Co2(double? grams, int sourcesWithoutCoefficient, string? missingReason, string view = "tech",
        string lang = HubLang.En)
    {
        var (lead, figure, unit) = Co2Parts(grams, sourcesWithoutCoefficient, missingReason, view, lang);
        if (grams is null)
            return figure + " " + unit;
        return T(lang, "recap.co2.text", ("value", (lead.Length > 0 ? lead + " " : "") + figure + " " + unit),
            ("tail", Co2Tail(sourcesWithoutCoefficient, view, lang)));
    }

    /// <summary>What a partial carbon figure leaves out, after a comma, or nothing when it is whole.</summary>
    public static string Co2Tail(int sourcesWithoutCoefficient, string view, string lang = HubLang.En)
    {
        if (sourcesWithoutCoefficient <= 0)
            return "";
        if (view == RecapViewNames.Pilotage)
            return T(lang, "recap.co2.tail.pilotage");
        return T(lang, "recap.co2.tail.tech." + HubLang.Plural(lang, sourcesWithoutCoefficient),
            ("count", sourcesWithoutCoefficient.ToString(CultureInfo.InvariantCulture)));
    }

    public static string Trend(string trend, double? change, string lang = HubLang.En)
    {
        if (trend is not ("new" or "gone" or "flat" or "up" or "down"))
            return "";
        if (trend is not ("up" or "down") || change is not { } value)
            return T(lang, "recap.trend." + trend);
        // The cast to long also turns -0 into 0, as String(-0) does in JavaScript.
        var percent = (long)Math.Floor(value * 100 + 0.5);
        return (value > 0 ? "+" : "") + percent.ToString(CultureInfo.InvariantCulture) +
               (lang == HubLang.Fr ? "\u202f%" : "%");
    }

    public static string Confirmation(string? state, string view = "tech", string lang = HubLang.En)
    {
        var pilotage = view == RecapViewNames.Pilotage ? ".pilotage" : "";
        return state switch
        {
            "confirmed" or "awaiting" => T(lang, "recap.confirm." + state + pilotage),
            "contradicted" or "came_back" => T(lang, "recap.confirm." + state),
            _ => ""
        };
    }

    public static string Truncated(string view, string lang = HubLang.En)
    {
        return T(lang, "recap.truncated." + Audience(view));
    }

    public static string SourcesWithoutVolume(IReadOnlyList<string> sources, string view, string lang = HubLang.En)
    {
        return T(lang, "recap.sources_note." + Audience(view), ("sources", string.Join(", ", sources)));
    }

    // Plain in both views: it says what happened, not how the Hub reads it.
    public static string LateSources(IReadOnlyList<string> sources, string lang = HubLang.En)
    {
        return T(lang, "recap.late_note", ("sources", string.Join(", ", sources)));
    }

    public static string Period(RecapPeriodInfo period, string lang = HubLang.En)
    {
        var days = period.Kind == "day"
            ? Day(period.From, lang)
            : T(lang, "recap.period.range", ("from", Day(period.From, lang)), ("to", Day(period.To, lang)));
        return period.InProgress ? T(lang, "recap.period.in_progress", ("days", days)) : days;
    }

    /// <summary>A recap day: <c>yyyy-MM-dd</c> in English, <c>29 oct. 2026</c> in French, as PSL.recapDay.</summary>
    public static string Day(string iso, string lang = HubLang.En)
    {
        if (lang != HubLang.Fr ||
            !DateOnly.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return iso;
        return (day.Day == 1 ? "1er" : day.Day.ToString(CultureInfo.InvariantCulture)) + " " +
               FrenchMonths[day.Month - 1] + " " + day.Year.ToString(CultureInfo.InvariantCulture);
    }

    public static string Incomplete(IReadOnlyList<string> reasons, string view = "tech", string lang = HubLang.En)
    {
        return reasons.Count > 0 ? T(lang, "recap.incomplete", ("reasons", Gaps(reasons, view, lang))) : "";
    }

    /// <summary>The gaps in words, comma-joined, as the incomplete line lists them.</summary>
    public static string Gaps(IReadOnlyList<string> reasons, string view = "tech", string lang = HubLang.En)
    {
        return string.Join(", ", reasons.Select(reason => Gap(reason, view, lang)));
    }

    public static string Notes(IReadOnlyList<string> notes, double? samplingRate, string view = "tech",
        string lang = HubLang.En)
    {
        if (notes.Count == 0)
            return "";
        return T(lang, "recap.notes", ("notes", string.Join(", ", notes.Select(note =>
            Gap(note, view, lang) + (note == "sampling" && samplingRate is { } rate
                ? T(lang, "recap.notes.rate", ("rate", Decimal(rate, lang)))
                : "")))));
    }

    // The rate as JavaScript's String writes it, with the decimal comma in French.
    private static string Decimal(double rate, string lang)
    {
        var text = rate.ToString(CultureInfo.InvariantCulture);
        return lang == HubLang.Fr ? text.Replace('.', ',') : text;
    }

    /// <summary>A gap in words. Engineering gets a reason a newer engine adds as it was sent, Management a plain stand-in.</summary>
    public static string Gap(string reason, string view = "tech", string lang = HubLang.En)
    {
        if (view == RecapViewNames.Pilotage)
            return T(lang, HubText.Has("gap.pilotage." + reason) ? "gap.pilotage." + reason : "gap.pilotage.other");
        return HubText.Has("gap.tech." + reason) ? T(lang, "gap.tech." + reason) : reason;
    }

    public static string Type(string findingType, string lang = HubLang.En)
    {
        return HubText.Has("finding.type." + findingType) ? T(lang, "finding.type." + findingType) : findingType;
    }

    /// <summary>The address when it is an absolute http or https one, else null: it comes from a daemon.</summary>
    public static string? HttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;
    }

    public static (string Io, string Findings, string Moved) Labels(string view, string lang = HubLang.En)
    {
        var prefix = "recap.label." + Audience(view) + ".";
        return (T(lang, prefix + "io"), T(lang, prefix + "findings"), T(lang, prefix + "moved"));
    }

    /// <summary>The volume's label inside a sentence, never a capital mid-line.</summary>
    public static string IoInSentence(string view, string lang = HubLang.En)
    {
        return T(lang, "recap.label." + Audience(view) + ".io_lower");
    }

    /// <summary>
    ///     An action owner as a person reads it: the name the SSO gave wins, with the identity as the
    ///     title; a machine or a hand-typed owner as it is; an identity turned into a name.
    /// </summary>
    public static (string Text, string? Title) Owner(string? owner, string? ownerDisplay)
    {
        if (!string.IsNullOrEmpty(ownerDisplay))
            return (ownerDisplay, string.IsNullOrEmpty(owner) ? null : owner);
        if (string.IsNullOrEmpty(owner))
            return ("", null);
        if (owner.StartsWith("machine:", StringComparison.Ordinal) ||
            owner.Any(c => char.IsWhiteSpace(c) || char.IsUpper(c)))
            return (owner, null);
        var identity = owner.StartsWith("local:", StringComparison.Ordinal) ? owner["local:".Length..] : owner;
        var local = identity.Split('@')[0];
        var name = string.Join(' ', local.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word =>
                string.Join('-', word.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Capitalize))));
        return name.Length > 0 ? (name, owner) : (owner, null);
    }

    private static string Capitalize(string word)
    {
        var first = char.IsSurrogatePair(word, 0) ? 2 : 1;
        return word[..first].ToUpperInvariant() + word[first..];
    }

    /// <summary>
    ///     HTML for a sentence in which the engine wrote code in backticks: those parts become
    ///     <c>code</c> elements, every part encoded. An odd count of backticks (a stray one) is left alone.
    /// </summary>
    public static string Prose(string text)
    {
        var encoder = HtmlEncoder.Default;
        var parts = text.Split('`');
        if (parts.Length % 2 == 0)
            return encoder.Encode(text);
        var html = new StringBuilder();
        for (var index = 0; index < parts.Length; index++)
        {
            if (parts[index].Length == 0)
                continue;
            html.Append(index % 2 == 1
                ? "<code class=\"code-inline\">" + encoder.Encode(parts[index]) + "</code>"
                : encoder.Encode(parts[index]));
        }

        return html.ToString();
    }

    /// <summary>The closing sentence of the Management "What to fix first" help, as in RECAP_HELP.</summary>
    public const string NamingHelp =
        "A team can name its services and steps in the Hub, so this report speaks its language.";

    public static string NamingHelpIn(string lang)
    {
        return T(lang, "recap.help.naming");
    }

    /// <summary>
    ///     RECAP_HELP of launcher.js: the seven sentences the page shows behind its "?", which a report
    ///     with no script prints in a closing section. The term of an entry is the heading it explains.
    /// </summary>
    public static IReadOnlyList<(string Term, string Text)> Help(string view, string lang = HubLang.En)
    {
        var (io, findings, moved) = Labels(view, lang);
        var help = "recap.help." + Audience(view) + ".";
        return
        [
            (T(lang, "recap.term.period"), T(lang, help + "period")),
            (io, T(lang, help + "io")),
            (T(lang, "recap.tile.co2"), T(lang, help + "co2")),
            (findings, T(lang, help + "findings")),
            (T(lang, "recap.tile.actions"), T(lang, help + "actions")),
            (T(lang, "recap.prio.title"), T(lang, help + "prio")),
            (moved, T(lang, help + "moved"))
        ];
    }
}
