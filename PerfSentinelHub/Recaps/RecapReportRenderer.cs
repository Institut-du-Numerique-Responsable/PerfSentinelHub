using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PerfSentinelHub.Api;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Localization;

namespace PerfSentinelHub.Recaps;

/// <summary>
///     A frozen recap as one self-contained HTML file: no request leaves it, no script reads the Hub, and
///     a reader with no script (a mail preview) still gets the light theme and the times in UTC. The
///     snapshot rides along in a JSON data island; every value shown goes through the HTML encoder.
/// </summary>
public static class RecapReportRenderer
{
    internal const string ThemeScript =
        "if(matchMedia('(prefers-color-scheme: dark)').matches)document.documentElement.dataset.theme='dark';";

    // Rewrites each <time> from UTC to the reader's own time and zone, keeping the UTC on hover.
    internal const string TimeScript =
        "document.querySelectorAll('time[datetime]').forEach(function(t){var d=new Date(t.getAttribute('datetime'));" +
        "if(isNaN(d))return;var p={};new Intl.DateTimeFormat('en-GB',{year:'numeric',month:'2-digit',day:'2-digit'," +
        "hour:'2-digit',minute:'2-digit',hour12:false,timeZoneName:'short'}).formatToParts(d).forEach(function(x){p[x.type]=x.value;});" +
        "t.title=t.textContent;t.textContent=p.year+'-'+p.month+'-'+p.day+' '+(p.hour==='24'?'00':p.hour)+':'+p.minute+' '+p.timeZoneName;});";

    // The same rewrite in the French order, day first, as PSL.localTime writes it in French.
    internal const string FrenchTimeScript =
        "document.querySelectorAll('time[datetime]').forEach(function(t){var d=new Date(t.getAttribute('datetime'));" +
        "if(isNaN(d))return;var p={};new Intl.DateTimeFormat('en-GB',{year:'numeric',month:'2-digit',day:'2-digit'," +
        "hour:'2-digit',minute:'2-digit',hour12:false,timeZoneName:'short'}).formatToParts(d).forEach(function(x){p[x.type]=x.value;});" +
        "t.title=t.textContent;t.textContent=p.day+'/'+p.month+'/'+p.year+' '+(p.hour==='24'?'00':p.hour)+':'+p.minute+' '+p.timeZoneName;});";

    private const int FirstPage = 10;
    private const string FoldedAt = "<details class=\"more\">";

    private const string SpanEnd = "</span>";
    private const string DivEnd = "</div>";
    private const string DivEndLine = "</div>\n";
    private const string ParagraphEndLine = "</p>\n";
    private const string CountName = "count";
    private const string InProgress = "in_progress";

    private static readonly Lazy<string> Style = new(() =>
        Resource("recap-tokens.css") + "\n" + Resource("recap-report.css"));

    private static readonly Lazy<string> Policy = new(() => PolicyWith(TimeScript));

    private static readonly Lazy<string> FrenchPolicy = new(() => PolicyWith(FrenchTimeScript));

    public static string ContentSecurityPolicy => Policy.Value;

    /// <summary>The policy of a page in <paramref name="lang" />: each language runs its own time script.</summary>
    public static string ContentSecurityPolicyFor(string lang)
    {
        return lang == HubLang.Fr ? FrenchPolicy.Value : Policy.Value;
    }

    private static string PolicyWith(string timeScript)
    {
        return "default-src 'none'; style-src 'unsafe-inline'; " +
               $"script-src {Hash(ThemeScript)} {Hash(timeScript)}; " +
               "img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
    }

    public static byte[] Render(RecapSnapshot snapshot, string lang = HubLang.En)
    {
        var page = new Page(snapshot, lang);
        var labels = RecapText.Labels(snapshot.View, lang);
        var html = new StringBuilder(32 * 1024);
        html.Append("<!doctype html>\n<html lang=\"").Append(lang).Append("\" data-theme=\"light\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(E(page.T("recap.page.title", ("period", RecapText.Period(snapshot.Period, lang)))))
            .Append("</title>\n")
            .Append("<style>").Append(Style.Value).Append("</style>\n")
            .Append("<script>").Append(ThemeScript).Append("</script>\n</head>\n<body>\n<main class=\"recap\">\n");
        Header(html, page);
        Tiles(html, page, labels);
        Priorities(html, page);
        Moves(html, page, labels);
        Help(html, page);
        Footer(html, page);
        html.Append("</main>\n<script type=\"application/json\" id=\"recap-data\">")
            .Append(JsonSerializer.Serialize(snapshot, HubJsonContext.Default.RecapSnapshot))
            .Append("</script>\n<script>").Append(lang == HubLang.Fr ? FrenchTimeScript : TimeScript)
            .Append("</script>\n</body>\n</html>\n");
        return Encoding.UTF8.GetBytes(html.ToString());
    }

    // What every part of the page reads: the snapshot, the language and the text in that language.
    private sealed record Page(RecapSnapshot Snapshot, string Lang)
    {
        public bool Management => Snapshot.View == RecapViewNames.Pilotage;

        public string T(string key, params (string Name, string Value)[] values)
        {
            return HubText.Get(Lang, key, values);
        }

        public string Number(long value)
        {
            return RecapText.Number(value, Lang);
        }

        public string Plural(long count)
        {
            return HubLang.Plural(Lang, count);
        }
    }

    // ---- the head of the page

    private static void Header(StringBuilder html, Page page)
    {
        var snapshot = page.Snapshot;
        var lang = page.Lang;
        var period = snapshot.Period;
        var previous = snapshot.Previous;
        html.Append("<header>\n").Append(Overline(page.T("recap.overline")))
            .Append("<h1>").Append(E(page.T("recap.title"))).Append("</h1>\n")
            .Append("<p class=\"period\"><span class=\"period-main\">").Append(E(RecapText.Period(period, lang)))
            .Append("</span><span class=\"period-tz\">UTC</span><span class=\"period-prev\">")
            .Append(E(Against(page, "recap.period.compared.", previous))).Append("</span></p>\n");
        Note(html, RecapText.Notes(period.Notes, period.SamplingRate, snapshot.View, lang));
        var previousNotes = RecapText.Notes(previous.Notes, previous.SamplingRate, snapshot.View, lang);
        Note(html, previousNotes.Length > 0 ? page.T("recap.notes.previous", ("notes", previousNotes)) : "");
        Note(html, Scope(page));
        Incomplete(html, page);
        html.Append("</header>\n");
    }

    // "compared with" or "since" the previous period, worded for a day or for a range of days.
    private static string Against(Page page, string prefix, RecapPeriodInfo previous)
    {
        return page.T(prefix + (previous.Kind == "day" ? "day" : "range"),
            ("period", RecapText.Period(previous, page.Lang)));
    }

    private static void Note(StringBuilder html, string text, string css = "notes")
    {
        if (text.Length > 0)
            html.Append("<p class=\"").Append(css).Append("\">").Append(E(text)).Append(ParagraphEndLine);
    }

    // The filters the report was cut with. Management reads values only, a named service by its name:
    // its words stay plain.
    private static string Scope(Page page)
    {
        var filters = page.Snapshot.Filters;
        var parts = page.Management
            ? new[] { filters.Environment, filters.ServiceLabel ?? filters.Service }.Where(value => value is not null)
                .Select(value => value!)
            : new[]
            {
                Named(page, "environment", filters.Environment), Named(page, "source", filters.SourceId),
                Named(page, "service", filters.Service), Named(page, "grouping", filters.Grouping)
            }.Where(value => value.Length > 0);
        var list = parts.ToList();
        return list.Count > 0 ? page.T("recap.scope", ("parts", string.Join(", ", list))) : "";
    }

    private static string Named(Page page, string name, string? value)
    {
        return value is null ? "" : page.T("recap.scope." + name, ("value", value));
    }

    private static void Incomplete(StringBuilder html, Page page)
    {
        var snapshot = page.Snapshot;
        var reasons = new List<string> { RecapText.Incomplete(snapshot.Period.Incomplete, snapshot.View, page.Lang) };
        if (snapshot.Previous.Incomplete.Count > 0)
            reasons.Add(page.T("recap.incomplete.previous",
                ("reasons", RecapText.Gaps(snapshot.Previous.Incomplete, snapshot.View, page.Lang))));
        reasons.RemoveAll(reason => reason.Length == 0);
        if (reasons.Count == 0)
            return;
        html.Append(
                "<div class=\"banner\" role=\"note\"><svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" ")
            .Append("stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">")
            .Append("<path d=\"M10.3 3.9 1.9 18a2 2 0 0 0 1.7 3h16.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z\"/>")
            .Append("<path d=\"M12 9v4M12 17h.01\"/></svg><p>")
            .Append(E(page.T("recap.banner", ("reasons", string.Join(page.T("recap.banner.join"), reasons)))))
            .Append("</p></div>\n");
    }

    // ---- the four tiles

    private static void Tiles(StringBuilder html, Page page, (string Io, string Findings, string Moved) labels)
    {
        var snapshot = page.Snapshot;
        var tiles = snapshot.Tiles;
        var actions = tiles.Actions;
        string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        html.Append("<div class=\"tiles\">\n");
        Tile(html, page, labels.Io, snapshot.Period.InProgress, Count(page, tiles.AvoidableIoOps) ?? "—",
            [PreviousText(page, Count(page, tiles.PreviousAvoidableIoOps))]);
        Co2Tile(html, page);
        Tile(html, page, labels.Findings, false, N(tiles.ActiveFindings),
        [
            PreviousText(page, N(tiles.PreviousActiveFindings)),
            page.T("recap.tile.acked." + (page.Management ? "pilotage." : "tech.") + page.Plural(tiles.AckedFindings),
                (CountName, N(tiles.AckedFindings)))
        ]);
        Tile(html, page, page.T("recap.tile.actions"), false,
            E(N(actions.Done)) + Unit(page.T("recap.tile.done." + page.Plural(actions.Done))),
        [
            page.T("recap.tile.actions.checked", ("confirmed", N(actions.Confirmed)),
                ("contradicted", N(actions.Contradicted)), ("came_back", N(actions.CameBack))),
            page.T("recap.tile.actions.open", (InProgress, N(actions.InProgress)), ("todo", N(actions.Todo)))
        ], true);
        html.Append(DivEndLine);
    }

    private static void Co2Tile(StringBuilder html, Page page)
    {
        var snapshot = page.Snapshot;
        var lang = page.Lang;
        var tiles = snapshot.Tiles;
        var without = tiles.Co2SourcesWithoutCoefficient;
        var (lead, figure, unit) =
            RecapText.Co2Parts(tiles.Co2Grams, without, tiles.Co2MissingReason, snapshot.View, lang);
        var known = tiles.Co2Grams is not null;
        var value = (lead.Length > 0 ? "<small>" + E(lead) + "</small>" : "") + E(figure) + Unit(unit);
        var previous = tiles.PreviousCo2Grams is { } grams
            ? RecapText.Co2Parts(grams, 0, null, RecapViewNames.Tech, lang).Figure
            : null;
        Tile(html, page, page.T("recap.tile.co2"), snapshot.Period.InProgress, value,
        [
            known ? page.T("recap.tile.magnitude", ("tail", RecapText.Co2Tail(without, snapshot.View, lang))) : "",
            previous is null ? "" : PreviousText(page, previous)
        ], true, RecapText.Co2(tiles.Co2Grams, without, tiles.Co2MissingReason, snapshot.View, lang));
    }

    // A tile's label, value and lines, each written where the tile shows it.
#pragma warning disable S107
    private static void Tile(
        StringBuilder html, Page page, string label, bool inProgress, string value, string[] subs,
        bool valueIsHtml = false, string? title = null)
#pragma warning restore S107
    {
        html.Append("<div class=\"tile\"><span class=\"tile-label\">").Append(E(label));
        if (inProgress)
            html.Append("<span class=\"chip\" data-tone=\"progress\">").Append(E(page.T("recap.chip.in_progress")))
                .Append(SpanEnd);
        html.Append("</span><span class=\"tile-value\"");
        if (title is not null)
            html.Append(" title=\"").Append(E(title)).Append('"');
        html.Append('>').Append(valueIsHtml ? value : E(value)).Append(SpanEnd);
        foreach (var sub in subs.Where(sub => sub.Length > 0))
            html.Append("<span class=\"tile-sub\">").Append(E(sub)).Append(SpanEnd);
        html.Append(DivEndLine);
    }

    private static string Unit(string text)
    {
        return "<span class=\"tile-unit\">" + E(text) + SpanEnd;
    }

    // "previous week 168,900", with the days it was cut to when this period was not over.
    private static string PreviousText(Page page, string? value)
    {
        if (value is null)
            return "";
        var previous = page.Snapshot.Previous;
        return page.T("recap.previous." + previous.Kind, ("cut", CutTo(page, previous.TruncatedToDays)),
            ("value", value));
    }

    private static string CutTo(Page page, int? days)
    {
        if (days is not > 0)
            return "";
        return page.T("recap.previous.cut." + page.Plural(days.Value),
            (CountName, days.Value.ToString(CultureInfo.InvariantCulture)));
    }

    private static string? Count(Page page, long? value)
    {
        return value is { } count ? page.Number(count) : null;
    }

    // ---- the priorities

    private static void Priorities(StringBuilder html, Page page)
    {
        var snapshot = page.Snapshot;
        var management = page.Management;
        var all = snapshot.Priorities;
        var hint = page.T("recap.prio.hint", ("io", RecapText.IoInSentence(snapshot.View, page.Lang))) +
                   (management ? "" : page.T("recap.prio.hint.co2")) +
                   (snapshot.Period.InProgress ? page.T("recap.prio.hint.closed") : "");
        html.Append("<section class=\"section\">\n").Append(Overline(page.T("recap.prio.overline")))
            .Append("<h2 class=\"section-head\">").Append(E(page.T("recap.prio.title"))).Append("<small>")
            .Append(E(hint)).Append("</small></h2>\n");
        if (all.Count == 0)
        {
            html.Append("<p class=\"empty\">").Append(E(page.T("recap.prio.empty"))).Append("</p>\n</section>\n");
            return;
        }

        var max = all[0].AvoidableIoOps ?? 0;
        var first = management ? all.Count : Math.Min(FirstPage, all.Count);
        PriorityList(html, page, 0, first, max);
        if (first < all.Count)
        {
            html.Append(FoldedAt).Append("<summary>")
                .Append(E(page.T("recap.prio.show_all", (CountName, all.Count.ToString(CultureInfo.InvariantCulture)))))
                .Append("</summary>\n");
            PriorityList(html, page, first, all.Count, max);
            html.Append("</details>\n");
        }

        if (management && snapshot.MorePriorities > 0)
            html.Append("<p class=\"more-note\">")
                .Append(E(page.T("recap.more.report." + page.Plural(snapshot.MorePriorities),
                    (CountName, snapshot.MorePriorities.ToString(CultureInfo.InvariantCulture)))))
                .Append(ParagraphEndLine);
        if (snapshot.Truncated)
            html.Append("<p class=\"sources-note\">").Append(E(RecapText.Truncated(snapshot.View, page.Lang)))
                .Append(ParagraphEndLine);
        html.Append("</section>\n");
    }

    private static void PriorityList(StringBuilder html, Page page, int from, int to, long max)
    {
        var snapshot = page.Snapshot;
        var management = page.Management;
        var all = snapshot.Priorities;
        html.Append("<ol class=\"rows\">\n");
        for (var index = from; index < to; index++)
        {
            var priority = all[index];
            // The ranking puts the rows with a volume first: the divider goes before the first one without,
            // and only after one with, as launcher.js dividesBefore.
            if (index > 0 && !HasVolume(priority) && HasVolume(all[index - 1]))
                html.Append("<li class=\"divider\">")
                    .Append(E(page.T("recap.prio.divider." + (management ? "pilotage" : "tech")))).Append("</li>\n");

            html.Append("<li class=\"row\"><span class=\"rank\">")
                .Append(priority.Rank.ToString(CultureInfo.InvariantCulture)).Append("</span>\n");
            if (management)
                PriorityPilotage(html, page, priority);
            else
                PriorityTech(html, page, priority);
            Figure(html, page, priority, max);
            if (!management)
                Fix(html, page, priority);
            html.Append("<div class=\"foot\">");
            if (!management)
                html.Append("<span class=\"facts\"><b>").Append(Detections(page, priority.Detections))
                    .Append("</b>").Append(E(page.T("recap.foot.detections"))).Append(SpanEnd);
            html.Append("<div class=\"action\">").Append(ActionHtml(page, priority.Action))
                .Append("</div></div></li>\n");
        }

        html.Append("</ol>\n");
    }

    private static bool HasVolume(RecapPriority priority)
    {
        return priority.AvoidableIoOps is > 0;
    }

    private static string Detections(Page page, long? value)
    {
        return Count(page, value) ?? "—";
    }

    private static void PriorityTech(StringBuilder html, Page page, RecapPriority priority)
    {
        html.Append("<div class=\"what\">");
        if (priority.Label is not null)
            html.Append("<p class=\"label\">").Append(E(priority.Label)).Append("</p>");
        html.Append("<div class=\"head\"><span class=\"chip\">").Append(E(RecapText.Type(priority.FindingType, page.Lang)))
            .Append(SpanEnd).Append(ServiceHead(priority.Service, priority.ServiceLabel))
            .Append("<span class=\"endpoint\">").Append(E(priority.Endpoint ?? "")).Append("</span></div>");
        if (priority.Suggestion is not null)
            html.Append("<p class=\"suggest\"").Append(EngineLang(page)).Append('>')
                .Append(RecapText.Prose(priority.Suggestion)).Append("</p>");
        html.Append(DivEndLine);
    }

    // The service by its business name, the technical one beside it once someone named it.
    private static string ServiceHead(string service, string? label)
    {
        var head = "<span class=\"strong\">" + E(label ?? service) + SpanEnd;
        return label is null ? head : head + "<code class=\"code-inline\">" + E(service) + "</code>";
    }

    // The label when someone named the endpoint, else a neutral title: a report never shows the endpoint.
    private static void PriorityPilotage(StringBuilder html, Page page, RecapPriority priority)
    {
        var service = priority.ServiceLabel ?? priority.Service;
        html.Append("<div class=\"what\">");
        if (priority.Label is not null)
            html.Append("<p class=\"label\">").Append(E(priority.Label)).Append("</p>");
        else
            html.Append("<p class=\"label\">").Append(E(Unnamed(page, service))).Append("</p><div class=\"where\">")
                .Append(E(page.T("recap.name_it"))).Append(DivEnd);
        html.Append("<p class=\"plain\">").Append(E(Plain(page, priority.FindingType, priority.Plain))).Append("</p>");
        if (priority.Label is not null)
            html.Append("<div class=\"where\">").Append(E(page.T("recap.where", ("service", service)))).Append(DivEnd);
        html.Append(DivEndLine);
    }

    // Read now, in the page's language; a type this Hub has no sentence for keeps the one its snapshot stored.
    private static string Plain(Page page, string findingType, string stored)
    {
        return RecapSentences.Knows(findingType) ? RecapSentences.For(findingType, page.Lang) : stored;
    }

    private static string Unnamed(Page page, string service)
    {
        return page.T("recap.unnamed", ("service", service));
    }

    private static void Figure(StringBuilder html, Page page, RecapPriority priority, long max)
    {
        html.Append("<div class=\"figure\">");
        if (HasVolume(priority))
        {
            var share = max > 0
                ? Math.Max(2, (int)Math.Floor((double)priority.AvoidableIoOps!.Value / max * 100 + 0.5))
                : 2;
            html.Append("<span class=\"figure-n\">").Append(Count(page, priority.AvoidableIoOps))
                .Append("</span><span class=\"figure-u\">")
                .Append(E(RecapText.IoInSentence(page.Snapshot.View, page.Lang)))
                .Append("</span><span class=\"bar\" aria-hidden=\"true\"><i style=\"width:")
                .Append(share.ToString(CultureInfo.InvariantCulture)).Append("%\"></i></span>");
        }
        else
        {
            html.Append("<span class=\"figure-n\">—</span><span class=\"figure-u\">")
                .Append(E(page.T(page.Management ? "recap.figure.none" : "recap.co2.no_avoidable_io")))
                .Append(SpanEnd);
        }

        html.Append(TrendHtml(page, priority.Trend, priority.Change)).Append(DivEndLine);
    }

    private static string TrendHtml(Page page, string trend, double? change)
    {
        var text = RecapText.Trend(trend, change, page.Lang);
        return text.Length == 0
            ? ""
            : "<span class=\"trend\" data-trend=\"" + E(trend) +
              "\" title=\"" + E(page.T("recap.trend.title")) + "\">" +
              E(text) + SpanEnd;
    }

    // ---- the folded technical detail (engineering)

    private static void Fix(StringBuilder html, Page page, RecapPriority priority)
    {
        html.Append("<details class=\"fix\"").Append(priority.Rank == 1 ? " open" : "")
            .Append("><summary>").Append(E(page.T("recap.fix.summary"))).Append("</summary><div class=\"fix-body\">");
        if (priority.Template is not null)
            html.Append("<pre class=\"code\">").Append(Lines(priority.Template)).Append("</pre>");
        FixBox(html, page, priority.SuggestedFix);
        CodeLocation(html, page, priority.CodeLocation);
        html.Append("<dl class=\"kv\"><dt>").Append(E(page.T("recap.fact.detections"))).Append("</dt><dd>")
            .Append(Detections(page, priority.Detections))
            .Append("</dd><dt>").Append(E(page.T("recap.fact.confidence"))).Append("</dt><dd>")
            .Append(E(priority.Confidence ?? "—"))
            .Append("</dd><dt>").Append(E(page.T("recap.fact.signature")))
            .Append("</dt><dd><code class=\"code-inline\">").Append(E(priority.Signature ?? ""))
            .Append("</code></dd></dl>");
        Events(html, page, priority.Action);
        html.Append("</div></details>\n");
    }

    private static void FixBox(StringBuilder html, Page page, RecapFix? fix)
    {
        if (fix is null)
            return;
        html.Append("<div class=\"fix-box\"><div class=\"sub-head\">").Append(E(page.T("recap.fix.suggested")));
        if (fix.Framework is not null)
            html.Append("<span class=\"chip\">").Append(E(fix.Framework)).Append(SpanEnd);
        html.Append("</div><p").Append(EngineLang(page)).Append('>').Append(RecapText.Prose(fix.Recommendation))
            .Append("</p>");
        if (fix.ReferenceUrl is not null)
        {
            var url = RecapText.HttpUrl(fix.ReferenceUrl);
            html.Append("<div class=\"fix-ref\">").Append(url is null
                    ? "<span>" + E(page.T("recap.fix.reference_at", ("url", fix.ReferenceUrl))) + SpanEnd
                    : "<a href=\"" + E(url) + "\" rel=\"noopener noreferrer\" target=\"_blank\">" +
                      E(page.T("recap.fix.reference")) + "</a>")
                .Append(DivEnd);
        }

        html.Append(DivEnd);
    }

    // The function on one line and path:line on the next, as a stack trace reads.
    private static void CodeLocation(StringBuilder html, Page page, RecapCodeLocation? location)
    {
        if (location is null)
            return;
        var at = string.Join(':', new[] { location.Filepath, location.Lineno?.ToString(CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrEmpty(part)));
        var lines = string.Join('\n', new[] { location.Function, at }.Where(part => !string.IsNullOrEmpty(part)));
        if (lines.Length > 0)
            html.Append("<div><div class=\"sub-head\">").Append(E(page.T("recap.fix.where")))
                .Append("</div><pre class=\"code\">").Append(Lines(lines))
                .Append("</pre></div>");
    }

    private static void Events(StringBuilder html, Page page, RecapAction? action)
    {
        if (action?.Events is not { Count: > 0 } events)
            return;
        html.Append("<div><div class=\"sub-head\">").Append(E(page.T("recap.events.title")))
            .Append("</div><ul class=\"events\">");
        foreach (var change in events)
        {
            var owner = RecapText.Owner(change.Owner, change.OwnerDisplay).Text;
            html.Append("<li>").Append(TimeTag(page, change.AtMs)).Append(E(
                (change.From is null ? "" : Todo(page, change.From) + " → ") + Todo(page, change.To) +
                (owner.Length > 0 ? ", " + owner : "") + page.T("recap.event.by", ("who", change.By)))).Append("</li>");
        }

        html.Append("</ul></div>");
    }

    private static string Todo(Page page, string status)
    {
        return status is "todo" or InProgress or "done" ? page.T("recap.status." + status) : status;
    }

    // An action, read only: a report cannot change it.
    private static string ActionHtml(Page page, RecapAction? action)
    {
        if (action is null)
            return State("none", page.T("recap.state.not_taken"));
        var owner = RecapText.Owner(action.Owner, action.OwnerDisplay);
        var chip = action.Status switch
        {
            "todo" => State("none", page.T("recap.state.todo")),
            InProgress => State(InProgress, page.T("recap.state.in_progress")),
            _ => DoneState(page, action.Confirmation)
        };
        var done = action.Status == "done" && action.DoneAtMs is not null;
        var at = TimeTag(page, done ? action.DoneAtMs!.Value : action.UpdatedAtMs);
        return chip + OwnerHtml(owner) +
               "<span class=\"when\">" + WithTime(page.T(done ? "recap.when.done" : "recap.when.updated"), at) +
               SpanEnd;
    }

    // The engine writes its suggestions in English: a French page says so to a screen reader.
    private static string EngineLang(Page page)
    {
        return page.Lang == HubLang.Fr ? " lang=\"en\"" : "";
    }

    // A sentence with a <time> in it: the words encoded, the element as it is.
    private static string WithTime(string sentence, string timeTag)
    {
        return E(sentence).Replace("{time}", timeTag, StringComparison.Ordinal);
    }

    private static string OwnerHtml((string Text, string? Title) owner)
    {
        if (owner.Text.Length == 0)
            return "";
        var title = owner.Title is null ? "" : " title=\"" + E(owner.Title) + "\"";
        return "<span class=\"owner\"" + title + ">" + E(owner.Text) + SpanEnd;
    }

    private static string DoneState(Page page, string? confirmation)
    {
        var label = RecapText.Confirmation(confirmation, page.Snapshot.View, page.Lang);
        return label.Length > 0
            ? State(confirmation!, page.T("recap.state.done_with", ("label", label.ToLowerInvariant())))
            : State("none", page.T("recap.state.done"));
    }

    private static string State(string kind, string text)
    {
        return "<span class=\"state\" data-state=\"" + E(kind) + "\">" + E(text) + SpanEnd;
    }

    // ---- what moved

    private static void Moves(StringBuilder html, Page page, (string Io, string Findings, string Moved) labels)
    {
        var snapshot = page.Snapshot;
        var lang = page.Lang;
        var moves = snapshot.Movements;
        var confirmed = moves.GoneWithConfirmedAction;
        (string Title, IReadOnlyList<RecapMove> Items, string Sub, string Tone)[] groups =
        [
            (page.T("recap.moves.new"), moves.New, "", ""),
            (page.T("recap.moves.gone.report"), moves.Gone,
                confirmed > 0
                    ? page.T("recap.moves.confirmed." + page.Plural(confirmed),
                        (CountName, confirmed.ToString(CultureInfo.InvariantCulture)))
                    : "", ""),
            (page.T("recap.moves.up"), moves.Up, "", "crit"),
            (page.T("recap.moves.down"), moves.Down, "", ""),
            (page.T("recap.moves.came_back"), moves.CameBack, "", "warn"),
            (page.T("recap.moves.done_still"), moves.DoneStillDetected, "", "crit")
        ];
        html.Append("<section class=\"section\">\n").Append(Overline("// " + labels.Moved.ToLowerInvariant()))
            .Append("<h2 class=\"section-head\">").Append(E(labels.Moved)).Append("<small>")
            .Append(E(Against(page, "recap.moves.since.", snapshot.Previous))).Append("</small></h2>\n");
        Note(html, MovedNote(page), "more-note");
        var shown = groups.Where(group => group.Items.Count > 0).ToList();
        if (shown.Count > 0)
        {
            html.Append("<div class=\"moves\">\n");
            foreach (var group in shown)
                MoveGroup(html, page, group.Title, group.Items, group.Sub, group.Tone);
            html.Append(DivEndLine);
        }

        if (snapshot.SourcesWithoutVolume.Count > 0)
            html.Append("<p class=\"sources-note\">")
                .Append(E(RecapText.SourcesWithoutVolume(snapshot.SourcesWithoutVolume, snapshot.View, lang)))
                .Append(ParagraphEndLine);
        if (snapshot.LateSources is { Count: > 0 } late)
            html.Append("<p class=\"sources-note\">").Append(E(RecapText.LateSources(late, lang)))
                .Append(ParagraphEndLine);
        html.Append("</section>\n");
    }

    // The first day of a period has no closed day to compare, which is not the same as nothing moving.
    private static string MovedNote(Page page)
    {
        if (page.Snapshot.Previous.TruncatedToDays == 0)
            return page.T("recap.moves.none_closed");
        return page.Snapshot.NothingMoved ? page.T("recap.moves.nothing") : "";
    }

    private static void MoveGroup(
        StringBuilder html, Page page, string title, IReadOnlyList<RecapMove> items, string sub, string tone)
    {
        html.Append("<div class=\"move\"").Append(tone.Length > 0 ? " data-tone=\"" + tone + "\"" : "")
            .Append("><h3>").Append(E(title)).Append("<span class=\"n\">")
            .Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(SpanEnd);
        if (sub.Length > 0)
            html.Append("<span class=\"sub\">").Append(E(sub)).Append(SpanEnd);
        html.Append("</h3><ul>");
        foreach (var item in items)
        {
            html.Append("<li><span class=\"who\">");
            if (page.Management)
                MoveWho(html, page, item);
            else
                html.Append("<span class=\"chip\">").Append(E(RecapText.Type(item.FindingType, page.Lang)))
                    .Append("</span><span class=\"strong\">").Append(E(item.Service))
                    .Append("</span><span class=\"endpoint\">").Append(E(item.Endpoint ?? "")).Append(SpanEnd);
            html.Append(SpanEnd).Append(TrendHtml(page, item.Trend, item.Change)).Append("</li>");
        }

        html.Append("</ul></div>\n");
    }

    private static void MoveWho(StringBuilder html, Page page, RecapMove item)
    {
        var service = item.ServiceLabel ?? item.Service;
        html.Append("<span class=\"plain\"><b>").Append(E((item.Label ?? Unnamed(page, service)) + ". "))
            .Append("</b>").Append(E(Plain(page, item.FindingType, item.Plain))).Append("</span><span class=\"where\">")
            .Append(E(item.Label is null ? page.T("recap.name_it") : page.T("recap.where.move", ("service", service))))
            .Append(SpanEnd);
    }

    // ---- the closing sections

    private static void Help(StringBuilder html, Page page)
    {
        html.Append("<section class=\"section help\">\n").Append(Overline(page.T("recap.help.overline")))
            .Append("<h2>").Append(E(page.T("recap.help.title"))).Append("</h2>\n<dl>\n");
        foreach (var (term, text) in RecapText.Help(page.Snapshot.View, page.Lang))
            html.Append("<dt>").Append(E(term)).Append("</dt><dd>").Append(E(text)).Append("</dd>\n");
        html.Append("</dl>\n</section>\n");
    }

    private static void Footer(StringBuilder html, Page page)
    {
        var view = page.T("recap.view." + (page.Management ? "pilotage" : "tech"));
        html.Append("<footer><p>")
            .Append(WithTime(page.T("recap.footer", ("view", view)), TimeTag(page, page.Snapshot.GeneratedAtMs)))
            .Append("</p></footer>\n");
    }

    // ---- helpers

    private static string Overline(string text)
    {
        return "<p class=\"overline-ruled\"><span class=\"overline\">" + E(text) +
               "</span><span class=\"overline-rule\"></span></p>\n";
    }

    // UTC in the markup, so a reader with no script sees the zone the report was cut in.
    private static string TimeTag(Page page, long ms)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        var shown = page.Lang == HubLang.Fr ? "dd'/'MM'/'yyyy HH:mm" : "yyyy-MM-dd HH:mm";
        return "<time datetime=\"" + at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\">" +
               at.ToString(shown, CultureInfo.InvariantCulture) + " UTC</time>";
    }

    // Encoded line by line, so a <pre> keeps real line breaks rather than &#xA;.
    private static string Lines(string text)
    {
        return string.Join('\n', text.Split('\n').Select(E));
    }

    private static string E(string text)
    {
        return HtmlEncoder.Default.Encode(text);
    }

    private static string Hash(string script)
    {
        return "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'";
    }

    private static string Resource(string name)
    {
        using var stream = typeof(RecapReportRenderer).Assembly.GetManifestResourceStream(name) ??
                           throw new InvalidOperationException($"The embedded resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
