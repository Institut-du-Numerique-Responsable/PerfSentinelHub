using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed partial class RecapReportRendererTests
{
    // The script the renderer pins, spelled out here so a change to it fails a test.
    private const string ThemeScript =
        "if(matchMedia('(prefers-color-scheme: dark)').matches)document.documentElement.dataset.theme='dark';";

    // HubJsonContext is internal to the Hub; its options are snake_case_lower, which these mirror.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string Html(RecapSnapshot snapshot)
    {
        return Encoding.UTF8.GetString(RecapReportRenderer.Render(snapshot));
    }

    private static string Management()
    {
        return Html(RecapViews.Frozen(RecapSnapshots.Distinctive()));
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
        return
        [
            .. Script().Matches(html).Where(match =>
                    !match.Groups[1].Value.Contains("type=\"application/json\"", StringComparison.Ordinal))
                .Select(match => match.Groups[2].Value)
        ];
    }

    private static string Island(string html)
    {
        return DataIsland().Match(html).Groups[1].Value;
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
        Assert.Equal(2, ScriptHash().Count(policy));
    }

    [Fact]
    public void Times_are_utc_without_script()
    {
        var html = Html(RecapSnapshots.Distinctive());

        Assert.Contains("<time datetime=\"2026-10-29T14:32:05Z\">2026-10-29 14:32 UTC</time>", html,
            StringComparison.Ordinal);
        Assert.Contains("Frozen on 2026-10-29 14:32 UTC.", Visible(html), StringComparison.Ordinal);
        var times = TimeElement().Matches(html);
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
    public void An_unlabelled_row_reads_as_an_unnamed_step_in_the_management_report()
    {
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Priorities = [RecapSnapshots.Priority(1, null)],
            Movements = new RecapMovements([RecapSnapshots.Move(null)], [], 0, [], [], [], [])
        };

        var html = Html(RecapViews.Frozen(snapshot));
        var visible = Visible(html);

        Assert.Contains("<p class=\"label\">An unnamed step in checkout-api</p><div class=\"where\">" +
                        "Name it in the Hub to see it here.</div>", html, StringComparison.Ordinal);
        Assert.Contains("An unnamed step in checkout-api. ", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(RecapSnapshots.Endpoint, html, StringComparison.Ordinal);
        Assert.DoesNotContain("\"endpoint\"", Island(html), StringComparison.Ordinal);
        Assert.DoesNotContain("<code", html, StringComparison.Ordinal);
        Assert.DoesNotContain(RecapSnapshots.Signature, html, StringComparison.Ordinal);
        Assert.DoesNotContain(RecapSnapshots.Template, html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_service_reads_by_its_name_in_the_management_report()
    {
        var named = RecapSnapshots.Priority(2) with { ServiceLabel = "Payment" };
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Priorities = [RecapSnapshots.Priority(1, null) with { ServiceLabel = "Payment" }, named],
            Movements = new RecapMovements(
                [RecapSnapshots.Move(null) with { ServiceLabel = "Payment" }],
                [RecapSnapshots.Move() with { ServiceLabel = "Payment" }], 0, [], [], [], [])
        };

        var html = Html(RecapViews.Frozen(snapshot));
        var visible = Visible(html);

        Assert.Contains("<p class=\"label\">An unnamed step in Payment</p><div class=\"where\">" +
                        "Name it in the Hub to see it here.</div>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"where\">in Payment</div>", html, StringComparison.Ordinal);
        Assert.Contains("An unnamed step in Payment. ", visible, StringComparison.Ordinal);
        Assert.Contains("In Payment.", visible, StringComparison.Ordinal);
        // The technical service stays in the data, as service, and nowhere a reader sees it.
        Assert.DoesNotContain("checkout-api", visible, StringComparison.Ordinal);
        Assert.Equal(Occurrences(html, "\"service\":\"checkout-api\""), Occurrences(html, "checkout-api"));
    }

    [Fact]
    public void An_unnamed_service_keeps_its_technical_name_in_the_management_report()
    {
        var html = Management();

        Assert.Contains("<div class=\"where\">in checkout-api</div>", html, StringComparison.Ordinal);
        Assert.Contains("In checkout-api.", Visible(html), StringComparison.Ordinal);
    }

    [Fact]
    public void The_management_scope_reads_a_named_service_by_its_name()
    {
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Filters = new RecapFilters("prod", null, "checkout-api", null, "Payment"),
            Priorities = [RecapSnapshots.Priority(1) with { ServiceLabel = "Payment" }],
            Movements = new RecapMovements([], [], 0, [], [], [], [])
        };

        var management = Visible(Html(RecapViews.Frozen(snapshot)));

        Assert.Contains("Scope: prod, Payment", management, StringComparison.Ordinal);
        Assert.DoesNotContain("checkout-api", management, StringComparison.Ordinal);
        Assert.Contains("Scope: environment prod, service checkout-api", Visible(Html(snapshot)),
            StringComparison.Ordinal);
        Assert.Contains("Scope: prod, checkout-api",
            Visible(Html(RecapViews.Frozen(snapshot with { Filters = snapshot.Filters with { ServiceLabel = null } }))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_report_shows_a_named_service_beside_its_technical_name()
    {
        var snapshot = RecapSnapshots.Distinctive() with
        {
            Priorities = [RecapSnapshots.Priority(1) with { ServiceLabel = "Payment" }]
        };

        var html = Html(snapshot);

        Assert.Contains("<span class=\"strong\">Payment</span><code class=\"code-inline\">checkout-api</code>", html,
            StringComparison.Ordinal);
        Assert.Contains("<span class=\"strong\">checkout-api</span>", Html(RecapSnapshots.Distinctive()),
            StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string value)
    {
        return (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    }

    [Fact]
    public void The_management_report_shows_the_owner_by_name_only()
    {
        var html = Html(RecapViews.Frozen(RecapSnapshots.Distinctive()));

        Assert.Contains("Alice Martin", Visible(html), StringComparison.Ordinal);
        Assert.DoesNotContain("alice.martin@acme.com", html, StringComparison.Ordinal);
        Assert.DoesNotContain("acme.com", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Alice.Martin@Acme.com", null, "Alice Martin")]
    [InlineData("Alice <alice.martin@acme.com>", null, "Alice")]
    [InlineData("Alice<alice.martin@acme.com>", null, "Alice")]
    [InlineData("alice.martin@acme.com", "alice.martin@acme.com", "Alice Martin")]
    [InlineData("local:Alice", null, "Alice")]
    public void The_management_report_never_shows_an_identity_however_the_owner_is_spelled(
        string owner, string? display, string name)
    {
        var snapshot = RecapSnapshots.Distinctive();
        var priority = snapshot.Priorities[0];
        snapshot = snapshot with
        {
            Priorities = [priority with { Action = priority.Action! with { Owner = owner, OwnerDisplay = display } }]
        };

        var html = Html(RecapViews.Frozen(snapshot));
        var withoutStyle = html[..html.IndexOf("<style>", StringComparison.Ordinal)] +
                           html[html.IndexOf("</style>", StringComparison.Ordinal)..];

        Assert.Contains("<span class=\"owner\">" + name + "</span>", html, StringComparison.Ordinal);
        // The stylesheet's @media rules aside, no character of an e-mail is left.
        Assert.DoesNotContain("@", withoutStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local:", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_report_keeps_the_owner_identity_on_hover()
    {
        Assert.Contains("<span class=\"owner\" title=\"alice.martin@acme.com\">Alice Martin</span>",
            Html(RecapSnapshots.Distinctive()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_management_report_uses_no_technical_word()
    {
        var html = Html(RecapViews.Frozen(Everything()));

        RecapSentencesTests.AssertPlain(Visible(html));
        foreach (Match title in TitleAttribute().Matches(html))
            RecapSentencesTests.AssertPlain(WebUtility.HtmlDecode(title.Groups[1].Value));
        var visible = Visible(html);
        Assert.Contains("Done, confirmed by the measurements", visible, StringComparison.Ordinal);
        Assert.Contains("Done, waiting for a full day of measurements", visible, StringComparison.Ordinal);
        Assert.Contains("order of magnitude, part of the activity has no CO2 estimate", visible,
            StringComparison.Ordinal);
        Assert.Contains("Incomplete: days with no data, a day not finished yet, a restart of the measuring tool, " +
                        "too many problems to count them all, another measurement gap", visible,
            StringComparison.Ordinal);
        Assert.Contains("No figures came from: svc-quiet.", visible, StringComparison.Ordinal);
        Assert.Contains("The Hub read the first 5,000 problems", visible, StringComparison.Ordinal);
        Assert.Contains("2 more in the engineering view.", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_report_keeps_the_engine_words()
    {
        var visible = Visible(Html(Everything()));

        Assert.Contains("Done, confirmed by the engine", visible, StringComparison.Ordinal);
        Assert.Contains("2 sources without a coefficient", visible, StringComparison.Ordinal);
        Assert.Contains("Incomplete: days the ledger does not cover", visible, StringComparison.Ordinal);
        Assert.Contains("Sources without a detection volume: svc-quiet.", visible, StringComparison.Ordinal);
    }

    // Every gap, every action state, a partial carbon figure, a cut read and an unnamed row.
    private static RecapSnapshot Everything()
    {
        // Seven rows: the management view keeps five and says two more are in the engineering view.
        var snapshot = RecapSnapshots.Distinctive(7);
        string?[] states = ["confirmed", "contradicted", "came_back", "awaiting", null, null, null];
        return snapshot with
        {
            Period = snapshot.Period with
            {
                Incomplete = ["uncovered_days", "open_day", "restart", "overflow", "brand_new"],
                Notes = ["sampling", "shed", "ingest_rejected", "multi_instance"],
                SamplingRate = 0.5
            },
            Previous = snapshot.Previous with { Incomplete = ["restart"], Notes = ["sampling"] },
            Tiles = snapshot.Tiles with { Co2SourcesWithoutCoefficient = 2 },
            Priorities =
            [
                .. snapshot.Priorities.Select((priority, index) => priority with
                {
                    Label = index == 4 ? null : priority.Label,
                    Action = priority.Action! with { Confirmation = states[index] }
                })
            ],
            Truncated = true
        };
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

        Assert.Contains("<pre class=\"code\">TechOnly.load\nsrc/TechOnly.java:42</pre>", html,
            StringComparison.Ordinal);
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
        Assert.Equal(10, Rows().Count(html[..folded]));
        Assert.Equal(2, Rows().Count(html[folded..html.LastIndexOf("</details>", StringComparison.Ordinal)]));
        Assert.Contains("<summary>Show all 12</summary>", html, StringComparison.Ordinal);
    }

    // The divider separates the rows with a volume from the ones without, so it needs one of each.
    [Theory]
    [InlineData(new long[] { 0, 0 }, 0)]
    [InlineData(new long[] { 5, 0, 0 }, 1)]
    [InlineData(new long[] { 5, 3 }, 0)]
    public void The_divider_follows_a_row_with_a_volume(long[] volumes, int dividers)
    {
        var snapshot = RecapSnapshots.Distinctive(volumes.Length);
        snapshot = snapshot with
        {
            Priorities =
            [.. snapshot.Priorities.Select((priority, index) => priority with { AvoidableIoOps = volumes[index] })]
        };

        Assert.Equal(dividers, Divider().Count(Html(snapshot)));
    }

    [Fact]
    public void The_management_report_lists_five_and_counts_the_rest()
    {
        var html = Html(RecapViews.Frozen(RecapSnapshots.Distinctive(12)));

        Assert.Equal(5, Rows().Count(html));
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

    [GeneratedRegex("<script type=\"application/json\" id=\"recap-data\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex DataIsland();

    [GeneratedRegex("'sha256-")]
    private static partial Regex ScriptHash();

    [GeneratedRegex("<time[^>]*>[^<]*</time>")]
    private static partial Regex TimeElement();

    [GeneratedRegex("title=\"([^\"]*)\"")]
    private static partial Regex TitleAttribute();

    [GeneratedRegex("<li class=\"divider\">Below, ")]
    private static partial Regex Divider();
}
