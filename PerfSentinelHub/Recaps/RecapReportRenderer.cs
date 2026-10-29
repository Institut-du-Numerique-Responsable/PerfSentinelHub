using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PerfSentinelHub.Api;

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

    private const int FirstPage = 10;
    private const string FoldedAt = "<details class=\"more\">";

    private static readonly Lazy<string> Style = new(() =>
        Resource("recap-tokens.css") + "\n" + Resource("recap-report.css"));

    private static readonly Lazy<string> Policy = new(() =>
        "default-src 'none'; style-src 'unsafe-inline'; " +
        $"script-src {Hash(ThemeScript)} {Hash(TimeScript)}; " +
        "img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'");

    public static string ContentSecurityPolicy => Policy.Value;

    public static byte[] Render(RecapSnapshot snapshot)
    {
        if (snapshot.View == "pilotage")
            snapshot = snapshot with { Priorities = [.. snapshot.Priorities.Select(ByName)] };
        var labels = RecapText.Labels(snapshot.View);
        var html = new StringBuilder(32 * 1024);
        html.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"light\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>Perf Sentinel recap, ").Append(E(RecapText.Period(snapshot.Period))).Append("</title>\n")
            .Append("<style>").Append(Style.Value).Append("</style>\n")
            .Append("<script>").Append(ThemeScript).Append("</script>\n</head>\n<body>\n<main class=\"recap\">\n");
        Header(html, snapshot);
        Tiles(html, snapshot, labels);
        Priorities(html, snapshot, labels);
        Moves(html, snapshot, labels);
        Help(html, snapshot);
        Footer(html, snapshot);
        html.Append("</main>\n<script type=\"application/json\" id=\"recap-data\">")
            .Append(JsonSerializer.Serialize(snapshot, HubJsonContext.Default.RecapSnapshot))
            .Append("</script>\n<script>").Append(TimeScript).Append("</script>\n</body>\n</html>\n");
        return Encoding.UTF8.GetBytes(html.ToString());
    }

    // A mailed report leaves the Hub: its owner is the name a person reads, never the identity behind it,
    // not even in a title or the data island.
    private static RecapPriority ByName(RecapPriority priority)
    {
        if (priority.Action is not { } action)
            return priority;
        var name = RecapText.Owner(action.Owner, action.OwnerDisplay).Text;
        return priority with
        {
            Action = action with { Owner = null, OwnerDisplay = name.Length > 0 ? name : null, Events = null }
        };
    }

    // ---- the head of the page

    private static void Header(StringBuilder html, RecapSnapshot snapshot)
    {
        var period = snapshot.Period;
        var previous = snapshot.Previous;
        html.Append("<header>\n").Append(Overline("// recap"))
            .Append("<h1>What wastes the most, and what moved</h1>\n")
            .Append("<p class=\"period\"><span class=\"period-main\">").Append(E(RecapText.Period(period)))
            .Append("</span><span class=\"period-tz\">UTC</span><span class=\"period-prev\">compared with ")
            .Append(E(RecapText.Period(previous))).Append("</span></p>\n");
        Note(html, RecapText.Notes(period.Notes, period.SamplingRate, snapshot.View));
        var previousNotes = RecapText.Notes(previous.Notes, previous.SamplingRate, snapshot.View);
        Note(html, previousNotes.Length > 0 ? "Previous period. " + previousNotes : "");
        Note(html, Scope(snapshot));
        Incomplete(html, snapshot);
        html.Append("</header>\n");
    }

    private static void Note(StringBuilder html, string text, string css = "notes")
    {
        if (text.Length > 0)
            html.Append("<p class=\"").Append(css).Append("\">").Append(E(text)).Append("</p>\n");
    }

    // The filters the report was cut with. Management reads values only: its words stay plain.
    private static string Scope(RecapSnapshot snapshot)
    {
        var filters = snapshot.Filters;
        var parts = snapshot.View == "pilotage"
            ? new[] { filters.Environment, filters.Service }.Where(value => value is not null).Select(value => value!)
            : new[]
            {
                Named("environment", filters.Environment), Named("source", filters.SourceId),
                Named("service", filters.Service), Named("grouping", filters.Grouping)
            }.Where(value => value.Length > 0);
        var list = parts.ToList();
        return list.Count > 0 ? "Scope: " + string.Join(", ", list) : "";
    }

    private static string Named(string name, string? value)
    {
        return value is null ? "" : name + " " + value;
    }

    private static void Incomplete(StringBuilder html, RecapSnapshot snapshot)
    {
        var reasons = new List<string> { RecapText.Incomplete(snapshot.Period.Incomplete, snapshot.View) };
        var previous = RecapText.Incomplete(snapshot.Previous.Incomplete, snapshot.View);
        if (previous.Length > 0)
            reasons.Add("previous period " + char.ToLowerInvariant(previous[0]) + previous[1..]);
        reasons.RemoveAll(reason => reason.Length == 0);
        if (reasons.Count == 0)
            return;
        html.Append("<div class=\"banner\" role=\"note\"><svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" ")
            .Append("stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\">")
            .Append("<path d=\"M10.3 3.9 1.9 18a2 2 0 0 0 1.7 3h16.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z\"/>")
            .Append("<path d=\"M12 9v4M12 17h.01\"/></svg><p>").Append(E(string.Join("; ", reasons)))
            .Append(". The comparison with the previous period is not exact.</p></div>\n");
    }

    // ---- the four tiles

    private static void Tiles(StringBuilder html, RecapSnapshot snapshot, (string Io, string Findings, string Moved) labels)
    {
        var tiles = snapshot.Tiles;
        var management = snapshot.View == "pilotage";
        var actions = tiles.Actions;
        html.Append("<div class=\"tiles\">\n");
        Tile(html, labels.Io, snapshot.Period.InProgress, Count(tiles.AvoidableIoOps) ?? "—",
            [PreviousText(snapshot, Count(tiles.PreviousAvoidableIoOps))]);
        Co2Tile(html, snapshot);
        Tile(html, labels.Findings, false, tiles.ActiveFindings.ToString(CultureInfo.InvariantCulture),
        [
            PreviousText(snapshot, tiles.PreviousActiveFindings.ToString(CultureInfo.InvariantCulture)),
            tiles.AckedFindings + (management ? " set aside" : " acknowledged") + ", left out"
        ]);
        Tile(html, "Actions", false, E(actions.Done.ToString(CultureInfo.InvariantCulture)) + Unit("done"),
        [
            $"{actions.Confirmed} confirmed, {actions.Contradicted} still detected, {actions.CameBack} came back",
            $"{actions.InProgress} in progress, {actions.Todo} to do"
        ], valueIsHtml: true);
        html.Append("</div>\n");
    }

    private static void Co2Tile(StringBuilder html, RecapSnapshot snapshot)
    {
        var tiles = snapshot.Tiles;
        var without = tiles.Co2SourcesWithoutCoefficient;
        var (lead, figure, unit) = RecapText.Co2Parts(tiles.Co2Grams, without, tiles.Co2MissingReason, snapshot.View);
        var known = tiles.Co2Grams is not null;
        var value = (lead.Length > 0 ? "<small>" + E(lead) + "</small>" : "") + E(figure) + Unit(unit);
        var previous = tiles.PreviousCo2Grams is { } grams ? RecapText.Co2Parts(grams, 0, null).Figure : null;
        Tile(html, "CO2 estimate", snapshot.Period.InProgress, value,
        [
            known ? "order of magnitude" + RecapText.Co2Tail(without, snapshot.View) : "",
            previous is null ? "" : PreviousText(snapshot, previous)
        ], valueIsHtml: true, title: RecapText.Co2(tiles.Co2Grams, without, tiles.Co2MissingReason, snapshot.View));
    }

    private static void Tile(
        StringBuilder html, string label, bool inProgress, string value, string[] subs, bool valueIsHtml = false,
        string? title = null)
    {
        html.Append("<div class=\"tile\"><span class=\"tile-label\">").Append(E(label));
        if (inProgress)
            html.Append("<span class=\"chip\" data-tone=\"progress\">in progress</span>");
        html.Append("</span><span class=\"tile-value\"");
        if (title is not null)
            html.Append(" title=\"").Append(E(title)).Append('"');
        html.Append('>').Append(valueIsHtml ? value : E(value)).Append("</span>");
        foreach (var sub in subs.Where(sub => sub.Length > 0))
            html.Append("<span class=\"tile-sub\">").Append(E(sub)).Append("</span>");
        html.Append("</div>\n");
    }

    private static string Unit(string text)
    {
        return "<span class=\"tile-unit\">" + E(text) + "</span>";
    }

    // "previous week 168,900", with the days it was cut to when this period was not over.
    private static string PreviousText(RecapSnapshot snapshot, string? value)
    {
        if (value is null)
            return "";
        var days = snapshot.Previous.TruncatedToDays;
        return "previous " + snapshot.Previous.Kind +
               (days is > 0 ? $" (first {days} day{(days == 1 ? "" : "s")})" : "") + " " + value;
    }

    private static string? Count(long? value)
    {
        return value?.ToString("#,0", CultureInfo.InvariantCulture);
    }

    // ---- the priorities

    private static void Priorities(StringBuilder html, RecapSnapshot snapshot, (string Io, string Findings, string Moved) labels)
    {
        var management = snapshot.View == "pilotage";
        var all = snapshot.Priorities;
        var hint = "ranked by " + Lower(labels.Io) + " over the period" + (management ? "" : ", CO2 never ranks") +
                   (snapshot.Period.InProgress ? ", trends on closed days" : "");
        html.Append("<section class=\"section\">\n").Append(Overline("// priorities"))
            .Append("<h2 class=\"section-head\">What to fix first<small>").Append(E(hint)).Append("</small></h2>\n");
        if (all.Count == 0)
        {
            html.Append("<p class=\"empty\">Nothing ranks in this period.</p>\n</section>\n");
            return;
        }

        var max = all[0].AvoidableIoOps ?? 0;
        var first = management ? all.Count : Math.Min(FirstPage, all.Count);
        PriorityList(html, snapshot, 0, first, max, labels);
        if (first < all.Count)
        {
            html.Append(FoldedAt).Append("<summary>Show all ").Append(all.Count.ToString(CultureInfo.InvariantCulture))
                .Append("</summary>\n");
            PriorityList(html, snapshot, first, all.Count, max, labels);
            html.Append("</details>\n");
        }

        if (management && snapshot.MorePriorities > 0)
            html.Append("<p class=\"more-note\">").Append(snapshot.MorePriorities.ToString(CultureInfo.InvariantCulture))
                .Append(" more in the engineering view.</p>\n");
        if (snapshot.Truncated)
            html.Append("<p class=\"sources-note\">").Append(E(RecapText.Truncated(snapshot.View))).Append("</p>\n");
        html.Append("</section>\n");
    }

    private static void PriorityList(
        StringBuilder html, RecapSnapshot snapshot, int from, int to, long max,
        (string Io, string Findings, string Moved) labels)
    {
        var management = snapshot.View == "pilotage";
        var all = snapshot.Priorities;
        html.Append("<ol class=\"rows\">\n");
        for (var index = from; index < to; index++)
        {
            var priority = all[index];
            // The ranking puts the rows with a volume first: the divider goes before the first one without,
            // and only after one with, as launcher.js dividesBefore.
            if (index > 0 && !HasVolume(priority) && HasVolume(all[index - 1]))
            {
                html.Append("<li class=\"divider\">")
                    .Append(management
                        ? "Below, slowness rather than extra work: ranked by severity"
                        : "Below, no avoidable I/O to count: ranked by severity").Append("</li>\n");
            }

            html.Append("<li class=\"row\"><span class=\"rank\">")
                .Append(priority.Rank.ToString(CultureInfo.InvariantCulture)).Append("</span>\n");
            if (management)
                PriorityPilotage(html, priority);
            else
                PriorityTech(html, priority);
            Figure(html, priority, max, labels, management);
            if (!management)
                Fix(html, priority);
            html.Append("<div class=\"foot\">");
            if (!management)
                html.Append("<span class=\"facts\"><b>").Append(Detections(priority.Detections))
                    .Append("</b> detections</span>");
            html.Append("<div class=\"action\">").Append(ActionHtml(priority.Action, snapshot.View))
                .Append("</div></div></li>\n");
        }

        html.Append("</ol>\n");
    }

    private static bool HasVolume(RecapPriority priority)
    {
        return priority.AvoidableIoOps is > 0;
    }

    private static string Detections(long? value)
    {
        return Count(value) ?? "—";
    }

    private static void PriorityTech(StringBuilder html, RecapPriority priority)
    {
        html.Append("<div class=\"what\">");
        if (priority.Label is not null)
            html.Append("<p class=\"label\">").Append(E(priority.Label)).Append("</p>");
        html.Append("<div class=\"head\"><span class=\"chip\">").Append(E(RecapText.Type(priority.FindingType)))
            .Append("</span><span class=\"strong\">").Append(E(priority.Service))
            .Append("</span><span class=\"endpoint\">").Append(E(priority.Endpoint ?? "")).Append("</span></div>");
        if (priority.Suggestion is not null)
            html.Append("<p class=\"suggest\">").Append(RecapText.Prose(priority.Suggestion)).Append("</p>");
        html.Append("</div>\n");
    }

    // The label when someone named the endpoint, else a neutral title: a report never shows the endpoint.
    private static void PriorityPilotage(StringBuilder html, RecapPriority priority)
    {
        html.Append("<div class=\"what\">");
        if (priority.Label is not null)
            html.Append("<p class=\"label\">").Append(E(priority.Label)).Append("</p>");
        else
            html.Append("<p class=\"label\">").Append(E(Unnamed(priority.Service))).Append("</p><p class=\"where\">")
                .Append(NameIt).Append("</p>");
        html.Append("<p class=\"plain\">").Append(E(priority.Plain)).Append("</p>");
        if (priority.Label is not null)
            html.Append("<div class=\"where\">in ").Append(E(priority.Service)).Append("</div>");
        html.Append("</div>\n");
    }

    private const string NameIt = "Name it in the Hub to see it here.";

    private static string Unnamed(string service)
    {
        return "An unnamed step in " + service;
    }

    private static void Figure(
        StringBuilder html, RecapPriority priority, long max, (string Io, string Findings, string Moved) labels,
        bool management)
    {
        html.Append("<div class=\"figure\">");
        if (HasVolume(priority))
        {
            var share = max > 0 ? Math.Max(2, (int)Math.Floor((double)priority.AvoidableIoOps!.Value / max * 100 + 0.5)) : 2;
            html.Append("<span class=\"figure-n\">").Append(Count(priority.AvoidableIoOps))
                .Append("</span><span class=\"figure-u\">").Append(E(Lower(labels.Io)))
                .Append("</span><span class=\"bar\" aria-hidden=\"true\"><i style=\"width:")
                .Append(share.ToString(CultureInfo.InvariantCulture)).Append("%\"></i></span>");
        }
        else
        {
            html.Append("<span class=\"figure-n\">—</span><span class=\"figure-u\">")
                .Append(management ? "nothing to count" : "no avoidable I/O").Append("</span>");
        }

        html.Append(TrendHtml(priority.Trend, priority.Change)).Append("</div>\n");
    }

    private static string TrendHtml(string trend, double? change)
    {
        var text = RecapText.Trend(trend, change);
        return text.Length == 0
            ? ""
            : "<span class=\"trend\" data-trend=\"" + E(trend) + "\" title=\"against the previous period, on closed days\">" +
              E(text) + "</span>";
    }

    // ---- the folded technical detail (engineering)

    private static void Fix(StringBuilder html, RecapPriority priority)
    {
        html.Append("<details class=\"fix\"").Append(priority.Rank == 1 ? " open" : "")
            .Append("><summary>How to fix</summary><div class=\"fix-body\">");
        if (priority.Template is not null)
            html.Append("<pre class=\"code\">").Append(Lines(priority.Template)).Append("</pre>");
        FixBox(html, priority.SuggestedFix);
        CodeLocation(html, priority.CodeLocation);
        html.Append("<dl class=\"kv\"><dt>detections</dt><dd>").Append(Detections(priority.Detections))
            .Append("</dd><dt>confidence</dt><dd>").Append(E(priority.Confidence ?? "—"))
            .Append("</dd><dt>signature</dt><dd><code class=\"code-inline\">").Append(E(priority.Signature ?? ""))
            .Append("</code></dd></dl>");
        Events(html, priority.Action);
        html.Append("</div></details>\n");
    }

    private static void FixBox(StringBuilder html, RecapFix? fix)
    {
        if (fix is null)
            return;
        html.Append("<div class=\"fix-box\"><div class=\"sub-head\">Suggested fix");
        if (fix.Framework is not null)
            html.Append("<span class=\"chip\">").Append(E(fix.Framework)).Append("</span>");
        html.Append("</div><p>").Append(RecapText.Prose(fix.Recommendation)).Append("</p>");
        if (fix.ReferenceUrl is not null)
        {
            var url = RecapText.HttpUrl(fix.ReferenceUrl);
            html.Append("<div class=\"fix-ref\">").Append(url is null
                ? "<span>Reference: " + E(fix.ReferenceUrl) + "</span>"
                : "<a href=\"" + E(url) + "\" rel=\"noopener noreferrer\" target=\"_blank\">Reference</a>").Append("</div>");
        }

        html.Append("</div>");
    }

    // The function on one line and path:line on the next, as a stack trace reads.
    private static void CodeLocation(StringBuilder html, RecapCodeLocation? location)
    {
        if (location is null)
            return;
        var at = string.Join(':', new[] { location.Filepath, location.Lineno?.ToString(CultureInfo.InvariantCulture) }
            .Where(part => !string.IsNullOrEmpty(part)));
        var lines = string.Join('\n', new[] { location.Function, at }.Where(part => !string.IsNullOrEmpty(part)));
        if (lines.Length > 0)
            html.Append("<div><div class=\"sub-head\">Where in the code</div><pre class=\"code\">").Append(Lines(lines))
                .Append("</pre></div>");
    }

    private static void Events(StringBuilder html, RecapAction? action)
    {
        if (action?.Events is not { Count: > 0 } events)
            return;
        html.Append("<div><div class=\"sub-head\">Action history</div><ul class=\"events\">");
        foreach (var change in events)
        {
            var owner = RecapText.Owner(change.Owner, change.OwnerDisplay).Text;
            html.Append("<li>").Append(TimeTag(change.AtMs)).Append(E(
                (change.From is null ? "" : Todo(change.From) + " → ") + Todo(change.To) +
                (owner.Length > 0 ? ", " + owner : "") + ", by " + change.By)).Append("</li>");
        }

        html.Append("</ul></div>");
    }

    private static string Todo(string status)
    {
        return status switch { "todo" => "to do", "in_progress" => "in progress", _ => status };
    }

    // An action, read only: a report cannot change it.
    private static string ActionHtml(RecapAction? action, string view)
    {
        if (action is null)
            return State("none", "Not taken yet");
        var owner = RecapText.Owner(action.Owner, action.OwnerDisplay);
        var chip = action.Status switch
        {
            "todo" => State("none", "To do"),
            "in_progress" => State("in_progress", "In progress"),
            _ => DoneState(action.Confirmation, view)
        };
        var done = action.Status == "done" && action.DoneAtMs is not null;
        return chip +
               (owner.Text.Length > 0
                   ? "<span class=\"owner\"" + (owner.Title is null ? "" : " title=\"" + E(owner.Title) + "\"") + ">" +
                     E(owner.Text) + "</span>"
                   : "") +
               "<span class=\"when\">" + (done ? "done " : "updated ") +
               TimeTag(done ? action.DoneAtMs!.Value : action.UpdatedAtMs) + "</span>";
    }

    private static string DoneState(string? confirmation, string view)
    {
        var label = RecapText.Confirmation(confirmation, view);
        return label.Length > 0 ? State(confirmation!, "Done, " + label.ToLowerInvariant()) : State("none", "Done");
    }

    private static string State(string kind, string text)
    {
        return "<span class=\"state\" data-state=\"" + E(kind) + "\">" + E(text) + "</span>";
    }

    // ---- what moved

    private static void Moves(StringBuilder html, RecapSnapshot snapshot, (string Io, string Findings, string Moved) labels)
    {
        var moves = snapshot.Movements;
        var confirmed = moves.GoneWithConfirmedAction;
        (string Title, IReadOnlyList<RecapMove> Items, string Sub, string Tone)[] groups =
        [
            ("New", moves.New, "", ""),
            ("Gone (presumed fixed: not detected this period)", moves.Gone,
                confirmed > 0 ? confirmed + " with a confirmed fix" : "", ""),
            ("Up", moves.Up, "", "crit"),
            ("Down", moves.Down, "", ""),
            ("Came back", moves.CameBack, "", "warn"),
            ("Done but still detected", moves.DoneStillDetected, "", "crit")
        ];
        html.Append("<section class=\"section\">\n").Append(Overline("// " + labels.Moved.ToLowerInvariant()))
            .Append("<h2 class=\"section-head\">").Append(E(labels.Moved)).Append("<small>since ")
            .Append(E(RecapText.Period(snapshot.Previous))).Append("</small></h2>\n");
        Note(html, MovedNote(snapshot), "more-note");
        var shown = groups.Where(group => group.Items.Count > 0).ToList();
        if (shown.Count > 0)
        {
            html.Append("<div class=\"moves\">\n");
            foreach (var group in shown)
                MoveGroup(html, group.Title, group.Items, group.Sub, group.Tone, snapshot.View == "pilotage");
            html.Append("</div>\n");
        }

        if (snapshot.SourcesWithoutVolume.Count > 0)
            html.Append("<p class=\"sources-note\">")
                .Append(E(RecapText.SourcesWithoutVolume(snapshot.SourcesWithoutVolume, snapshot.View))).Append("</p>\n");
        html.Append("</section>\n");
    }

    // The first day of a period has no closed day to compare, which is not the same as nothing moving.
    private static string MovedNote(RecapSnapshot snapshot)
    {
        if (snapshot.Previous.TruncatedToDays == 0)
            return "No day of this period has closed yet, so nothing is compared with the previous one.";
        return snapshot.NothingMoved ? "Nothing moved since the previous period." : "";
    }

    private static void MoveGroup(
        StringBuilder html, string title, IReadOnlyList<RecapMove> items, string sub, string tone, bool management)
    {
        html.Append("<div class=\"move\"").Append(tone.Length > 0 ? " data-tone=\"" + tone + "\"" : "")
            .Append("><h3>").Append(E(title)).Append("<span class=\"n\">")
            .Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append("</span>");
        if (sub.Length > 0)
            html.Append("<span class=\"sub\">").Append(E(sub)).Append("</span>");
        html.Append("</h3><ul>");
        foreach (var item in items)
        {
            html.Append("<li><span class=\"who\">");
            if (management)
                MoveWho(html, item);
            else
                html.Append("<span class=\"chip\">").Append(E(RecapText.Type(item.FindingType)))
                    .Append("</span><span class=\"strong\">").Append(E(item.Service))
                    .Append("</span><span class=\"endpoint\">").Append(E(item.Endpoint ?? "")).Append("</span>");
            html.Append("</span>").Append(TrendHtml(item.Trend, item.Change)).Append("</li>");
        }

        html.Append("</ul></div>\n");
    }

    private static void MoveWho(StringBuilder html, RecapMove item)
    {
        html.Append("<span class=\"plain\"><b>").Append(E((item.Label ?? Unnamed(item.Service)) + ". ")).Append("</b>")
            .Append(E(item.Plain)).Append("</span><span class=\"where\">")
            .Append(item.Label is null ? NameIt : "In " + E(item.Service) + ".").Append("</span>");
    }

    // ---- the closing sections

    private static void Help(StringBuilder html, RecapSnapshot snapshot)
    {
        html.Append("<section class=\"section help\">\n").Append(Overline("// help")).Append("<h2>How to read this report</h2>\n<dl>\n");
        foreach (var (term, text) in RecapText.Help(snapshot.View))
            html.Append("<dt>").Append(E(term)).Append("</dt><dd>").Append(E(text)).Append("</dd>\n");
        html.Append("</dl>\n</section>\n");
    }

    private static void Footer(StringBuilder html, RecapSnapshot snapshot)
    {
        html.Append("<footer><p>Frozen on ").Append(TimeTag(snapshot.GeneratedAtMs)).Append(". ")
            .Append(snapshot.View == "pilotage" ? "Management" : "Engineering")
            .Append(" view. CO2 is an order-of-magnitude estimate.</p></footer>\n");
    }

    // ---- helpers

    private static string Overline(string text)
    {
        return "<p class=\"overline-ruled\"><span class=\"overline\">" + E(text) +
               "</span><span class=\"overline-rule\"></span></p>\n";
    }

    // UTC in the markup, so a reader with no script sees the zone the report was cut in.
    private static string TimeTag(long ms)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return "<time datetime=\"" + at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "\">" +
               at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC</time>";
    }

    private static string Lower(string text)
    {
        return char.ToLowerInvariant(text[0]) + text[1..];
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
