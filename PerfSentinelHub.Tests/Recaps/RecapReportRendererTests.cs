using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed partial class RecapReportRendererTests
{
    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new()
    { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // The script the renderer pins, spelled out here so a change to it fails a test.
    private const string ThemeScript =
        "if(matchMedia('(prefers-color-scheme: dark)').matches)document.documentElement.dataset.theme='dark';";

    private static string Html(RecapSnapshot snapshot)
    {
        return Encoding.UTF8.GetString(RecapReportRenderer.Render(snapshot));
    }

    private static string Management()
    {
        return Html(RecapViews.Pilotage(RecapSnapshots.Distinctive()));
    }

    /// <summary>What a reader sees: no script, no style, no tag, entities decoded.</summary>
    private static string Visible(string html)
    {
        var body = ScriptOrStyle().Replace(html, " ");
        // A <time> sits inside a sentence; any other tag breaks the words around it.
        return WebUtility.HtmlDecode(Tag().Replace(TimeTag().Replace(body, ""), " "));
    }

    private static List<string> ExecutableScripts(string html)
    {
        return [.. Script().Matches(html).Where(match => !match.Groups[1].Value.Contains("type=\"application/json\"", StringComparison.Ordinal))
            .Select(match => match.Groups[2].Value)];
    }

    private static string Island(string html)
    {
        return Regex.Match(html, "<script type=\"application/json\" id=\"recap-data\">(.*?)</script>",
            RegexOptions.Singleline).Groups[1].Value;
    }

    private static string Hash(string script)
    {
        return "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'";
    }

    [Fact]
    public void The_report_loads_nothing_from_outside()
    {
        foreach (var html in new[] { Html(RecapSnapshots.Distinctive(12)), Management() })
        {
            Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
            foreach (Match match in UrlCall().Matches(html))
                Assert.StartsWith("data:", html[(match.Index + 4)..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_data_island_reads_back_as_the_snapshot()
    {
        var snapshot = RecapSnapshots.Distinctive(3);

        var back = JsonSerializer.Deserialize<RecapSnapshot>(Island(Html(snapshot)), Wire);

        Assert.NotNull(back);
        Assert.Equal(JsonSerializer.Serialize(snapshot, Wire), JsonSerializer.Serialize(back, Wire));
    }

    [Fact]
    public void Markup_in_a_finding_is_escaped()
    {
        const string template = "</script><img src=x onerror=alert(1)>";
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Priorities = [RecapSnapshots.Priority(1, template: template)]
        };

        var html = Html(snapshot);

        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;/script&gt;", html, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize<RecapSnapshot>(Island(html), Wire);
        Assert.Equal(template, back!.Priorities[0].Template);
    }

    [Fact]
    public void A_javascript_reference_url_is_not_a_link()
    {
        var priority = RecapSnapshots.Priority(1) with
        {
            SuggestedFix = new RecapFix("fix it", null, "javascript:alert(1)")
        };

        var html = Html(RecapSnapshots.Distinctive() with { Priorities = [priority] });

        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reference: javascript:alert(1)", Visible(html), StringComparison.Ordinal);
    }

    [Fact]
    public void An_http_reference_url_is_a_link()
    {
        Assert.Contains("<a href=\"https://example.test/ref\"", Html(RecapSnapshots.Distinctive()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_csp_hash_matches_the_inline_script()
    {
        var scripts = ExecutableScripts(Html(RecapSnapshots.Distinctive()));

        // The theme script and the time script, and nothing else can run.
        Assert.Equal(2, scripts.Count);
        Assert.Equal(ThemeScript, scripts[0]);
        Assert.Contains(Hash(ThemeScript), RecapReportRenderer.ContentSecurityPolicy, StringComparison.Ordinal);
        Assert.StartsWith(
            "default-src 'none'; style-src 'unsafe-inline'; script-src ", RecapReportRenderer.ContentSecurityPolicy,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "; img-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'self'",
            RecapReportRenderer.ContentSecurityPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public void The_csp_lists_both_inline_scripts()
    {
        var policy = RecapReportRenderer.ContentSecurityPolicy;

        foreach (var script in ExecutableScripts(Html(RecapSnapshots.Distinctive())))
            Assert.Contains(Hash(script), policy, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(policy, "'sha256-").Count);
    }

    [Fact]
    public void Times_are_utc_without_script()
    {
        var html = Html(RecapSnapshots.Distinctive());

        Assert.Contains("<time datetime=\"2026-10-29T14:32:05Z\">2026-10-29 14:32 UTC</time>", html,
            StringComparison.Ordinal);
        Assert.Contains("Frozen on 2026-10-29 14:32 UTC.", Visible(html), StringComparison.Ordinal);
        var times = Regex.Matches(html, "<time[^>]*>[^<]*</time>");
        Assert.NotEmpty(times);
        foreach (Match time in times)
            Assert.Matches(@"^<time datetime=""\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ"">\d{4}-\d\d-\d\d \d\d:\d\d UTC</time>$",
                time.Value);
    }

    [Fact]
    public void The_management_report_carries_no_engineering_value()
    {
        var html = Management();

        foreach (var value in RecapSnapshots.Values)
            Assert.DoesNotContain(value, html, StringComparison.Ordinal);
        var island = Island(html);
        foreach (var key in new[]
                 {
                     "signature", "endpoint", "template", "suggestion", "suggested_fix", "code_location",
                     "confidence", "events"
                 })
            Assert.DoesNotContain($"\"{key}\"", island, StringComparison.Ordinal);
        Assert.Contains("Label OK", Visible(html), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlabelled_priority_shows_its_endpoint_in_the_management_report()
    {
        var snapshot = RecapSnapshots.Distinctive() with { Priorities = [RecapSnapshots.Priority(1, label: null)] };

        var visible = Visible(Html(RecapViews.Pilotage(snapshot)));

        Assert.Contains(RecapSnapshots.Endpoint, visible, StringComparison.Ordinal);
        Assert.DoesNotContain(RecapSnapshots.Signature, visible, StringComparison.Ordinal);
        Assert.DoesNotContain(RecapSnapshots.Template, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void The_management_report_uses_no_technical_word()
    {
        var visible = Visible(Management()).ToLowerInvariant();

        foreach (var word in new[]
                     { "query", "database", "endpoint", "broker", "pool", "connection", "sql", "http", "i/o", "finding" })
            Assert.DoesNotContain(word, visible);
    }

    [Fact]
    public void The_engineering_report_shows_the_engineering_values()
    {
        var visible = Visible(Html(RecapSnapshots.Distinctive()));

        foreach (var value in RecapSnapshots.Values)
            Assert.Contains(value, visible, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_report_puts_code_in_code_elements()
    {
        var html = Html(RecapSnapshots.Distinctive());

        Assert.Contains("<pre class=\"code\">TechOnly.load\nsrc/TechOnly.java:42</pre>", html, StringComparison.Ordinal);
        Assert.Contains("<code class=\"code-inline\">sig-TECH-1</code>", html, StringComparison.Ordinal);
        Assert.Contains("<code class=\"code-inline\">batch_size</code>", html, StringComparison.Ordinal);
        Assert.Contains("Alice Martin", Visible(html), StringComparison.Ordinal);
    }

    [Fact]
    public void An_incomplete_period_says_the_comparison_is_not_exact()
    {
        var snapshot = RecapSnapshots.Distinctive();
        var incomplete = snapshot with { Period = snapshot.Period with { Incomplete = ["open_day"] } };

        var visible = Visible(Html(incomplete));

        Assert.Contains("Incomplete: a day still open. The comparison with the previous period is not exact.", visible,
            StringComparison.Ordinal);
        Assert.Contains("role=\"note\"", Html(incomplete), StringComparison.Ordinal);
        Assert.DoesNotContain("not exact", Visible(Html(snapshot)), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_moved_says_so()
    {
        var snapshot = RecapSnapshots.Distinctive() with
        {
            NothingMoved = true,
            Movements = new RecapMovements([], [], 0, [], [], [], [])
        };

        Assert.Contains("Nothing moved since the previous period.", Visible(Html(snapshot)), StringComparison.Ordinal);
    }

    [Fact]
    public void Co2_reads_as_an_estimate()
    {
        var visible = Visible(Html(RecapSnapshots.Distinctive()));

        Assert.Contains("~2 kg", visible, StringComparison.Ordinal);
        Assert.Contains("order of magnitude", visible, StringComparison.Ordinal);
        Assert.Contains("CO2 is an order-of-magnitude estimate.", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gone_list_is_a_presumption()
    {
        Assert.Contains("Gone (presumed fixed: not detected this period)", Visible(Html(RecapSnapshots.Distinctive())),
            StringComparison.Ordinal);
        Assert.Contains("Gone (presumed fixed: not detected this period)", Visible(Management()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_report_shows_ten_then_the_rest_folded()
    {
        var html = Html(RecapSnapshots.Distinctive(12));

        var folded = html.IndexOf("<details class=\"more\">", StringComparison.Ordinal);
        Assert.True(folded > 0);
        Assert.Equal(10, Rows().Matches(html[..folded]).Count);
        Assert.Equal(2, Rows().Matches(html[folded..html.LastIndexOf("</details>", StringComparison.Ordinal)]).Count);
        Assert.Contains("<summary>Show all 12</summary>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_management_report_lists_five_and_counts_the_rest()
    {
        var html = Html(RecapViews.Pilotage(RecapSnapshots.Distinctive(12)));

        Assert.Equal(5, Rows().Matches(html).Count);
        Assert.DoesNotContain("<details class=\"more\">", html, StringComparison.Ordinal);
        Assert.Contains("7 more in the engineering view.", Visible(html), StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_explains_how_to_read_it_and_which_view_it_is()
    {
        var tech = Visible(Html(RecapSnapshots.Distinctive()));
        var management = Visible(Management());

        Assert.Contains("How to read this report", tech, StringComparison.Ordinal);
        Assert.Contains("Gone means not detected this period", tech, StringComparison.Ordinal);
        Assert.Contains("Engineering view.", tech, StringComparison.Ordinal);
        Assert.Contains("\"Gone\" means it was not seen this period", management, StringComparison.Ordinal);
        Assert.Contains("Management view.", management, StringComparison.Ordinal);
        Assert.Contains("Sources without a detection volume: svc-quiet.", tech, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_is_light_without_script_and_titled_by_its_period()
    {
        var html = Html(RecapSnapshots.Distinctive());

        Assert.Contains("<html lang=\"en\" data-theme=\"light\">", html, StringComparison.Ordinal);
        Assert.Contains("<title>Perf Sentinel recap, 2026-10-26 to 2026-11-01</title>", html, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<li class=""row[ """"]")]
    private static partial Regex Rows();

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex("</?time[^>]*>")]
    private static partial Regex TimeTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex("<script([^>]*)>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex Script();

    [GeneratedRegex(@"url\(")]
    private static partial Regex UrlCall();
}
