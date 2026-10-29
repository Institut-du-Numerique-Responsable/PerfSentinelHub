using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;

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
    private static readonly Dictionary<string, string> GapLabels = new(StringComparer.Ordinal)
    {
        ["uncovered_days"] = "days the ledger does not cover",
        ["open_day"] = "a day still open",
        ["restart"] = "a daemon restart",
        ["sampling"] = "sampled traces",
        ["shed"] = "traces shed under memory pressure",
        ["ingest_rejected"] = "rejected ingest",
        ["overflow"] = "a full ledger",
        ["multi_instance"] = "several instances behind one source"
    };

    // GAP_LABEL_PILOTAGE: the same reasons in a Management reader's words.
    private static readonly Dictionary<string, string> PilotageGapLabels = new(StringComparer.Ordinal)
    {
        ["uncovered_days"] = "days with no data",
        ["open_day"] = "a day not finished yet",
        ["restart"] = "a restart of the measuring tool",
        ["sampling"] = "only part of the traffic was looked at",
        ["shed"] = "some activity was dropped under load",
        ["ingest_rejected"] = "some data was refused",
        ["overflow"] = "too many problems to count them all",
        ["multi_instance"] = "several copies of one service"
    };

    private static readonly Dictionary<string, string> TypeLabels = new(StringComparer.Ordinal)
    {
        ["n_plus_one_sql"] = "N+1 SQL",
        ["n_plus_one_http"] = "N+1 HTTP",
        ["n_plus_one_messaging"] = "N+1 messaging",
        ["redundant_sql"] = "Redundant SQL",
        ["redundant_http"] = "Redundant HTTP",
        ["slow_sql"] = "Slow SQL",
        ["slow_http"] = "Slow HTTP",
        ["slow_messaging"] = "Slow messaging",
        ["excessive_fanout"] = "Excessive fanout",
        ["chatty_service"] = "Chatty service",
        ["pool_saturation"] = "Pool saturation",
        ["serialized_calls"] = "Serialized calls"
    };

    private static readonly (string Io, string Findings, string Moved) TechLabels =
        ("Avoidable I/O", "Active findings", "What moved");

    private static readonly (string Io, string Findings, string Moved) PilotageLabels =
        ("Unnecessary operations", "Open problems", "What changed");

    /// <summary>The carbon estimate in three pieces, as <c>co2Parts</c> returns them.</summary>
    public static (string Lead, string Figure, string Unit) Co2Parts(
        double? grams, int sourcesWithoutCoefficient, string? missingReason, string view = "tech")
    {
        if (grams is null && view == "pilotage")
            return ("", "—", "not available");
        if (grams is not { } value)
            return ("", "—", missingReason == "no_avoidable_io" ? "no avoidable I/O" : "no coefficient from the engine");
        // A nonzero estimate never rounds to zero; "at least" would contradict "< 1 g".
        if (value is > 0 and < 1)
            return ("", "< 1 g", "CO2e");
        var kilos = value >= 1000;
        // toPrecision(3) on the kilos, Math.round on the grams.
        var shown = kilos
            ? double.Parse((value / 1000).ToString("E2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : Math.Floor(value + 0.5);
        var number = shown.ToString("#,0.###", CultureInfo.InvariantCulture);
        return (sourcesWithoutCoefficient > 0 ? "at least" : "", "~" + number + (kilos ? " kg" : " g"), "CO2e");
    }

    public static string Co2(double? grams, int sourcesWithoutCoefficient, string? missingReason, string view = "tech")
    {
        var (lead, figure, unit) = Co2Parts(grams, sourcesWithoutCoefficient, missingReason, view);
        if (grams is null)
            return figure + " " + unit;
        return (lead.Length > 0 ? lead + " " : "") + figure + " " + unit + ", estimate" +
               Co2Tail(sourcesWithoutCoefficient, view);
    }

    /// <summary>What a partial carbon figure leaves out, after a comma, or nothing when it is whole.</summary>
    public static string Co2Tail(int sourcesWithoutCoefficient, string view)
    {
        if (sourcesWithoutCoefficient <= 0)
            return "";
        return view == "pilotage"
            ? ", part of the activity has no CO2 estimate"
            : ", " + sourcesWithoutCoefficient + (sourcesWithoutCoefficient == 1 ? " source" : " sources") +
              " without a coefficient";
    }

    public static string Trend(string trend, double? change)
    {
        switch (trend)
        {
            case "new": return "new";
            case "gone": return "gone";
            case "flat": return "stable";
            case "up" or "down": break;
            default: return "";
        }

        if (change is not { } value)
            return trend;
        // The cast to long also turns -0 into 0, as String(-0) does in JavaScript.
        var percent = (long)Math.Floor(value * 100 + 0.5);
        return (value > 0 ? "+" : "") + percent.ToString(CultureInfo.InvariantCulture) + "%";
    }

    public static string Confirmation(string? state, string view = "tech")
    {
        var pilotage = view == "pilotage";
        return state switch
        {
            "confirmed" => pilotage ? "Confirmed by the measurements" : "Confirmed by the engine",
            "contradicted" => "Still detected",
            "came_back" => "Came back",
            "awaiting" => pilotage ? "Waiting for a full day of measurements" : "Waiting for a sealed day",
            _ => ""
        };
    }

    public static string Truncated(string view)
    {
        return "The Hub read the first 5,000 " + (view == "pilotage" ? "problems" : "findings") +
               " and stopped there. Narrow the filters to see the others.";
    }

    public static string SourcesWithoutVolume(IReadOnlyList<string> sources, string view)
    {
        return (view == "pilotage" ? "No figures came from: " : "Sources without a detection volume: ") +
               string.Join(", ", sources) + ".";
    }

    public static string Period(RecapPeriodInfo period)
    {
        var days = period.Kind == "day" ? period.From : period.From + " to " + period.To;
        return days + (period.InProgress ? ", in progress" : "");
    }

    public static string Incomplete(IReadOnlyList<string> reasons, string view = "tech")
    {
        return reasons.Count > 0 ? "Incomplete: " + string.Join(", ", reasons.Select(reason => Gap(reason, view))) : "";
    }

    public static string Notes(IReadOnlyList<string> notes, double? samplingRate, string view = "tech")
    {
        if (notes.Count == 0)
            return "";
        return "Note: " + string.Join(", ", notes.Select(note =>
            Gap(note, view) + (note == "sampling" && samplingRate is { } rate
                ? " (rate " + rate.ToString(CultureInfo.InvariantCulture) + ")"
                : "")));
    }

    /// <summary>A gap in words. Engineering gets a reason a newer engine adds as it was sent, Management a plain stand-in.</summary>
    public static string Gap(string reason, string view = "tech")
    {
        if (view == "pilotage")
            return PilotageGapLabels.TryGetValue(reason, out var plain) ? plain : "another measurement gap";
        return GapLabels.TryGetValue(reason, out var label) ? label : reason;
    }

    public static string Type(string findingType)
    {
        return TypeLabels.TryGetValue(findingType, out var label) ? label : findingType;
    }

    /// <summary>The address when it is an absolute http or https one, else null: it comes from a daemon.</summary>
    public static string? HttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? value
            : null;
    }

    public static (string Io, string Findings, string Moved) Labels(string view)
    {
        return view == "pilotage" ? PilotageLabels : TechLabels;
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
        if (owner.StartsWith("machine:", StringComparison.Ordinal) || owner.Any(c => char.IsWhiteSpace(c) || char.IsUpper(c)))
            return (owner, null);
        var identity = owner.StartsWith("local:", StringComparison.Ordinal) ? owner["local:".Length..] : owner;
        var local = identity.Split('@')[0];
        var name = string.Join(' ', local.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => string.Join('-', word.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Capitalize))));
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

    /// <summary>
    ///     RECAP_HELP of launcher.js: the seven sentences the page shows behind its "?", which a report
    ///     with no script prints in a closing section. The term of an entry is the heading it explains.
    /// </summary>
    public static IReadOnlyList<(string Term, string Text)> Help(string view)
    {
        var (io, findings, moved) = Labels(view);
        var pilotage = view == "pilotage";
        return
        [
            ("The period", pilotage
                ? "A week runs Monday to Sunday, counted in UTC days. While it is not over, comparisons only use the days already finished. Times elsewhere on the page are in your local time, with the zone."
                : "A week runs Monday to Sunday and a month is a calendar month, both counted in UTC days. While the period is in progress, trends compare its closed days with the same days of the previous period; today counts in the tiles only. Times elsewhere on the page are in your local time, with the zone."),
            (io, pilotage
                ? "Work the application did for nothing on this period, counted by Perf Sentinel. Measured, not estimated."
                : "Extra database, HTTP and messaging calls the engine counted as avoidable on this period, summed over the detections. Measured, not estimated."),
            ("CO2 estimate", pilotage
                ? "A rough estimate of the carbon this unnecessary work cost. It gives a scale, not an exact figure."
                : "An order of magnitude, never a ranking key: each finding's avoidable calls times the day's grams per avoidable call the daemon reports. \"At least\" when some sources send no coefficient."),
            (findings, pilotage
                ? "Problems seen on this period that the team has not set aside."
                : "Findings seen on this period and not acknowledged. Acknowledged ones are counted apart and left out of the priorities."),
            ("Actions", pilotage
                ? "What the team said it would fix, and whether the measurements confirm it."
                : "Declared by the team, checked by the engine: confirmed after a sealed day with no detection, still detected when it shows up after the done day."),
            ("What to fix first", pilotage
                ? "The problems that cost the most come first."
                : "Findings with avoidable calls first, by volume; then the others by severity. Trends compare closed days only."),
            (moved, pilotage
                ? "\"Gone\" means it was not seen this period: probably fixed, but not proven."
                : "Gone means not detected this period: a presumption, not proof that the fix shipped.")
        ];
    }
}
