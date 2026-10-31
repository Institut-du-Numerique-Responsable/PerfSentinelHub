using PerfSentinelHub.Localization;
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
            { 0.5, 0, null, "< 1 g CO2e, estimate" },
            { 0.0002, 0, null, "< 1 g CO2e, estimate" },
            { 0.4, 1, null, "< 1 g CO2e, estimate, 1 source without a coefficient" },
            { 0, 0, null, "~0 g CO2e, estimate" },
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

    [Theory]
    [InlineData("uncovered_days", "days with no data")]
    [InlineData("open_day", "a day not finished yet")]
    [InlineData("restart", "a restart of the measuring tool")]
    [InlineData("sampling", "only part of the traffic was looked at")]
    [InlineData("shed", "some activity was dropped under load")]
    [InlineData("ingest_rejected", "some data was refused")]
    [InlineData("overflow", "too many problems to count them all")]
    [InlineData("multi_instance", "several copies of one service")]
    [InlineData("brand_new", "another measurement gap")]
    public void Management_gaps_match_the_launcher(string reason, string expected)
    {
        Assert.Equal(expected, RecapText.Gap(reason, "pilotage"));
    }

    [Fact]
    public void Management_text_matches_the_launcher_helpers()
    {
        Assert.Equal("Incomplete: a day not finished yet, another measurement gap",
            RecapText.Incomplete(["open_day", "x"], "pilotage"));
        Assert.Equal("Note: only part of the traffic was looked at (rate 0.5), some activity was dropped under load",
            RecapText.Notes(["sampling", "shed"], 0.5, "pilotage"));
        Assert.Equal("at least ~2 kg CO2e, estimate, part of the activity has no CO2 estimate",
            RecapText.Co2(2000, 1, null, "pilotage"));
        Assert.Equal("< 1 g CO2e, estimate, part of the activity has no CO2 estimate",
            RecapText.Co2(0.4, 2, null, "pilotage"));
        Assert.Equal("~300 g CO2e, estimate", RecapText.Co2(300, 0, null, "pilotage"));
        Assert.Equal("— not available", RecapText.Co2(null, 0, "no_coefficient", "pilotage"));
        Assert.Equal("— not available", RecapText.Co2(null, 0, "no_avoidable_io", "pilotage"));
        Assert.Equal(("", "—", "not available"), RecapText.Co2Parts(null, 0, "no_coefficient", "pilotage"));
        Assert.Equal(", part of the activity has no CO2 estimate", RecapText.Co2Tail(1, "pilotage"));
        Assert.Equal(", 2 sources without a coefficient", RecapText.Co2Tail(2, "tech"));
        Assert.Equal(", 1 source without a coefficient", RecapText.Co2Tail(1, "tech"));
        Assert.Equal("", RecapText.Co2Tail(0, "pilotage"));
        Assert.Equal("Confirmed by the measurements", RecapText.Confirmation("confirmed", "pilotage"));
        Assert.Equal("Still detected", RecapText.Confirmation("contradicted", "pilotage"));
        Assert.Equal("Came back", RecapText.Confirmation("came_back", "pilotage"));
        Assert.Equal("Waiting for a full day of measurements", RecapText.Confirmation("awaiting", "pilotage"));
        Assert.Equal("", RecapText.Confirmation("later", "pilotage"));
        Assert.Equal("The Hub read the first 5,000 problems and stopped there. Narrow the filters to see the others.",
            RecapText.Truncated("pilotage"));
        Assert.Equal("The Hub read the first 5,000 findings and stopped there. Narrow the filters to see the others.",
            RecapText.Truncated("tech"));
        Assert.Equal("No figures came from: a, b.", RecapText.SourcesWithoutVolume(["a", "b"], "pilotage"));
        Assert.Equal("Sources without a detection volume: a, b.", RecapText.SourcesWithoutVolume(["a", "b"], "tech"));
        Assert.Equal("Left out, as they recorded nothing until after this period: a, b.",
            RecapText.LateSources(["a", "b"]));
        // The Engineering view keeps the engine's words.
        Assert.Equal("Incomplete: a day still open, x", RecapText.Incomplete(["open_day", "x"]));
        Assert.Equal("Confirmed by the engine", RecapText.Confirmation("confirmed"));
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
        Assert.EndsWith(" " + RecapText.NamingHelp, pilotage[5].Text, StringComparison.Ordinal);
        Assert.Equal("A team can name its services and steps in the Hub, so this report speaks its language.",
            RecapText.NamingHelp);
        Assert.DoesNotContain(RecapText.NamingHelp, tech[5].Text, StringComparison.Ordinal);
        Assert.Equal("Avoidable I/O", tech[1].Term);
        Assert.Equal("Unnecessary operations", pilotage[1].Term);
    }

    // The French twins of the facts above: each expected string is what launcher.js returns with the shown
    // language fr, pinned in tests/text.test.js too.
    public static TheoryData<double?, int, string?, string, string> FrenchCo2Cases()
    {
        return new TheoryData<double?, int, string?, string, string>
        {
            { 2000, 1, null, "tech", "au moins ~2 kg CO2e, estimation, 1 source sans coefficient" },
            { 2500, 2, null, "tech", "au moins ~2,5 kg CO2e, estimation, 2 sources sans coefficient" },
            { 1234567, 0, null, "tech", "~1\u202f230 kg CO2e, estimation" },
            { 999.5, 0, null, "tech", "~1\u202f000 g CO2e, estimation" },
            { null, 0, "no_coefficient", "tech", "— aucun coefficient du moteur" },
            { null, 0, "no_avoidable_io", "tech", "— aucune I/O évitable" },
            { null, 0, "no_coefficient", "pilotage", "— non disponible" },
            { 0.4, 2, null, "pilotage", "< 1 g CO2e, estimation, une partie de l'activité n'a pas d'estimation CO2" }
        };
    }

    [Theory]
    [MemberData(nameof(FrenchCo2Cases))]
    public void Co2_matches_the_launcher_in_french(double? grams, int without, string? reason, string view,
        string expected)
    {
        Assert.Equal(expected, RecapText.Co2(grams, without, reason, view, HubLang.Fr));
    }

    [Theory]
    [InlineData("up", 0.35, "+35\u202f%")]
    [InlineData("down", -0.2, "-20\u202f%")]
    [InlineData("down", -0.004, "0\u202f%")]
    [InlineData("up", null, "en hausse")]
    [InlineData("flat", 0.0, "stable")]
    [InlineData("new", null, "nouveau")]
    [InlineData("gone", null, "disparu")]
    [InlineData("unknown", null, "")]
    public void Trend_matches_the_launcher_in_french(string trend, double? change, string expected)
    {
        Assert.Equal(expected, RecapText.Trend(trend, change, HubLang.Fr));
    }

    [Fact]
    public void Periods_notes_and_labels_match_the_launcher_in_french()
    {
        const string fr = HubLang.Fr;
        Assert.Equal("du 26 oct. 2026 au 1er nov. 2026, en cours",
            RecapText.Period(new RecapPeriodInfo("week", "2026-10-26", "2026-11-01", true, [], [], null, null), fr));
        Assert.Equal("29 oct. 2026",
            RecapText.Period(new RecapPeriodInfo("day", "2026-10-29", "2026-10-29", false, [], [], null, null), fr));
        Assert.Equal("Période incomplète\u00a0: une journée pas encore terminée, un autre manque de mesure",
            RecapText.Incomplete(["open_day", "x"], "pilotage", fr));
        Assert.Equal("Période incomplète\u00a0: un jour encore ouvert, x", RecapText.Incomplete(["open_day", "x"], "tech", fr));
        Assert.Equal(
            "Note\u00a0: seule une partie du trafic a été examinée (taux 0,5), une partie de l'activité a été écartée sous la charge",
            RecapText.Notes(["sampling", "shed"], 0.5, "pilotage", fr));
        Assert.Equal("Confirmée par le moteur", RecapText.Confirmation("confirmed", "tech", fr));
        Assert.Equal("En attente d'une journée complète de mesures", RecapText.Confirmation("awaiting", "pilotage", fr));
        Assert.Equal(
            "Le Hub a lu les 5\u202f000 premiers problèmes et s'est arrêté là. Resserrez les filtres pour voir les autres.",
            RecapText.Truncated("pilotage", fr));
        Assert.Equal("Sources sans volume de détection\u00a0: a, b.", RecapText.SourcesWithoutVolume(["a", "b"], "tech", fr));
        Assert.Equal(("Opérations inutiles", "Problèmes ouverts", "Ce qui a changé"), RecapText.Labels("pilotage", fr));
        Assert.Equal(("I/O évitables", "Findings actifs", "Ce qui a bougé"), RecapText.Labels("tech", fr));
        Assert.Equal("SQL redondant", RecapText.Type("redundant_sql", fr));
        Assert.Equal("blocking_wait", RecapText.Type("blocking_wait", fr));
        Assert.Equal("brand_new", RecapText.Gap("brand_new", "tech", fr));
    }

    [Fact]
    public void Help_gives_the_launchers_seven_french_sentences_for_each_view()
    {
        var pilotage = RecapText.Help("pilotage", HubLang.Fr);

        Assert.Equal(7, RecapText.Help("tech", HubLang.Fr).Count);
        Assert.Equal(7, pilotage.Count);
        Assert.Equal("Opérations inutiles", pilotage[1].Term);
        Assert.EndsWith(" " + RecapText.NamingHelpIn(HubLang.Fr), pilotage[5].Text, StringComparison.Ordinal);
        Assert.Equal("Une équipe peut nommer ses applications et ses étapes dans le Hub, pour que ce rapport parle sa langue.",
            RecapText.NamingHelpIn(HubLang.Fr));
        Assert.Equal(RecapText.NamingHelp, RecapText.NamingHelpIn(HubLang.En));
    }
}
