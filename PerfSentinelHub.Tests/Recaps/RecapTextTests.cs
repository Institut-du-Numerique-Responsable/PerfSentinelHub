using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

// Every expected string below is what launcher.js returns for the same input (checked with node
// against PSL when pinned), so the frozen report and the #/recap page cannot read differently.
public sealed class RecapTextTests
{
    public static TheoryData<double?, int, string?, string> Co2Cases()
    {
        return new TheoryData<double?, int, string?, string>
        {
            { 300, 0, null, "~300 g CO2e, estimate" },
            { 2000, 1, null, "at least ~2 kg CO2e, estimate, 1 source without a coefficient" },
            { 2500, 2, null, "at least ~2.5 kg CO2e, estimate, 2 sources without a coefficient" },
            { null, 0, "no_coefficient", "— no coefficient from the engine" },
            { null, 0, "no_avoidable_io", "— no avoidable I/O" },
            { 0.5, 0, null, "~1 g CO2e, estimate" },
            { 999.5, 0, null, "~1,000 g CO2e, estimate" },
            { 1234567, 0, null, "~1,230 kg CO2e, estimate" },
            { 1234.5, 0, null, "~1.23 kg CO2e, estimate" },
            { 1005, 0, null, "~1 kg CO2e, estimate" },
            { 1e6, 0, null, "~1,000 kg CO2e, estimate" }
        };
    }

    [Theory]
    [MemberData(nameof(Co2Cases))]
    public void Co2_matches_the_launcher(double? grams, int without, string? reason, string expected)
    {
        Assert.Equal(expected, RecapText.Co2(grams, without, reason));
    }

    [Theory]
    [InlineData("up", 0.35, "+35%")]
    [InlineData("down", -0.2, "-20%")]
    [InlineData("up", 0.125, "+13%")]
    [InlineData("down", -0.125, "-12%")]
    [InlineData("up", 0.001, "+0%")]
    [InlineData("down", -0.004, "0%")]
    [InlineData("up", null, "up")]
    [InlineData("flat", 0.0, "stable")]
    [InlineData("new", null, "new")]
    [InlineData("gone", null, "gone")]
    [InlineData("unknown", null, "")]
    public void Trend_matches_the_launcher(string trend, double? change, string expected)
    {
        Assert.Equal(expected, RecapText.Trend(trend, change));
    }

    [Theory]
    [InlineData("confirmed", "Confirmed by the engine")]
    [InlineData("contradicted", "Still detected")]
    [InlineData("came_back", "Came back")]
    [InlineData("awaiting", "Waiting for a sealed day")]
    [InlineData("later", "")]
    [InlineData(null, "")]
    public void Confirmation_matches_the_launcher(string? state, string expected)
    {
        Assert.Equal(expected, RecapText.Confirmation(state));
    }

    [Fact]
    public void Periods_notes_and_gaps_match_the_launcher()
    {
        Assert.Equal("2026-10-29, in progress",
            RecapText.Period(new RecapPeriodInfo("day", "2026-10-29", "2026-10-29", true, [], [], null, null)));
        Assert.Equal("2026-10-26 to 2026-11-01",
            RecapText.Period(new RecapPeriodInfo("week", "2026-10-26", "2026-11-01", false, [], [], null, null)));
        Assert.Equal("Incomplete: a day still open, x", RecapText.Incomplete(["open_day", "x"]));
        Assert.Equal("", RecapText.Incomplete([]));
        Assert.Equal("Note: sampled traces (rate 0.5), traces shed under memory pressure",
            RecapText.Notes(["sampling", "shed"], 0.5));
        Assert.Equal("Note: sampled traces (rate 1)", RecapText.Notes(["sampling"], 1));
        Assert.Equal("Note: zzz", RecapText.Notes(["zzz"], null));
        Assert.Equal("", RecapText.Notes([], 0.5));
        Assert.Equal("a daemon restart", RecapText.Gap("restart"));
        Assert.Equal("brand_new", RecapText.Gap("brand_new"));
    }

    [Fact]
    public void Finding_types_match_the_launcher()
    {
        Assert.Equal("N+1 SQL", RecapText.Type("n_plus_one_sql"));
        Assert.Equal("Serialized calls", RecapText.Type("serialized_calls"));
        Assert.Equal("blocking_wait", RecapText.Type("blocking_wait"));
    }

    [Theory]
    [InlineData("http://a.b/c", "http://a.b/c")]
    [InlineData("https://a.b/c?d=1", "https://a.b/c?d=1")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://x.test/a", null)]
    [InlineData("/relative", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void HttpUrl_accepts_only_absolute_http_addresses(string? value, string? expected)
    {
        Assert.Equal(expected, RecapText.HttpUrl(value));
    }

    [Fact]
    public void Labels_match_the_launcher_per_view()
    {
        Assert.Equal(("Avoidable I/O", "Active findings", "What moved"), RecapText.Labels("tech"));
        Assert.Equal(("Unnecessary operations", "Open problems", "What changed"), RecapText.Labels("pilotage"));
    }

    [Theory]
    [InlineData("alice.martin@acme.com", null, "Alice Martin", "alice.martin@acme.com")]
    [InlineData("local:alice", null, "Alice", "local:alice")]
    [InlineData("jean-eric.dupont@acme.fr", null, "Jean-Eric Dupont", "jean-eric.dupont@acme.fr")]
    [InlineData("jean-eric.dupont@acme.fr", "Jean-Éric Dupont", "Jean-Éric Dupont", "jean-eric.dupont@acme.fr")]
    [InlineData("Équipe paiement", null, "Équipe paiement", null)]
    [InlineData("machine:ci", null, "machine:ci", null)]
    [InlineData("Élodie", null, "Élodie", null)]
    [InlineData("alice_martin", null, "Alice Martin", "alice_martin")]
    [InlineData(null, null, "", null)]
    [InlineData("", null, "", null)]
    public void Owner_matches_the_launcher(string? owner, string? display, string text, string? title)
    {
        Assert.Equal((text, title), RecapText.Owner(owner, display));
    }

    [Fact]
    public void Prose_turns_backticks_into_code_and_encodes_the_rest()
    {
        Assert.Equal("set <code class=\"code-inline\">a&lt;b</code> now", RecapText.Prose("set `a<b` now"));
        // An odd count of backticks is left alone, as the page leaves it.
        Assert.Equal("a `b", RecapText.Prose("a `b"));
        Assert.Equal("&lt;b&gt;", RecapText.Prose("<b>"));
    }

    [Fact]
    public void Help_gives_the_launchers_seven_sentences_for_each_view()
    {
        var tech = RecapText.Help("tech");
        var pilotage = RecapText.Help("pilotage");

        Assert.Equal(7, tech.Count);
        Assert.Equal(7, pilotage.Count);
        Assert.Contains("Gone means not detected this period: a presumption, not proof that the fix shipped.",
            tech.Select(entry => entry.Text));
        Assert.Contains("\"Gone\" means it was not seen this period: probably fixed, but not proven.",
            pilotage.Select(entry => entry.Text));
        Assert.Equal("Avoidable I/O", tech[1].Term);
        Assert.Equal("Unnecessary operations", pilotage[1].Term);
    }
}
