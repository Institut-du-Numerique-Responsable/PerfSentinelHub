using System.Text.RegularExpressions;
using PerfSentinelHub.Localization;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapSentencesTests
{
    private const string Unknown = "blocking_wait";

    /// <summary>
    ///     The engine's words a Management reader must not meet. "service" is left out: a service's name
    ///     is a value the Management view shows, and the plain sentences add it below.
    /// </summary>
    internal static readonly string[] TechnicalWords =
    [
        "query", "database", "endpoint", "broker", "pool", "connection", "sql", "http", "i/o", "finding",
        "signature", "engine", "daemon", "ledger", "coefficient", "trace", "ingest", "sampl", "source"
    ];

    internal static readonly string[] Reasons =
        ["uncovered_days", "open_day", "restart", "sampling", "shed", "ingest_rejected", "overflow", "multi_instance"];

    internal static void AssertPlain(string text)
    {
        // "engine" as a whole word: "engineering" names the other view, which a Management reader may meet.
        foreach (var word in TechnicalWords)
            Assert.DoesNotMatch(@"(?i)" + (word == "engine" ? @"\bengine\b" : Regex.Escape(word)), text);
    }

    public static TheoryData<string> AllTypes()
    {
        return [.. FindingLabels.Types, Unknown];
    }

    [Fact]
    public void Every_engine_type_has_its_own_sentence()
    {
        var fallback = RecapSentences.For(Unknown);
        var sentences = FindingLabels.Types.Select(RecapSentences.For).ToList();

        Assert.Equal(sentences.Count, sentences.Distinct().Count());
        Assert.DoesNotContain(fallback, sentences);
    }

    [Fact]
    public void An_unknown_type_gets_the_fallback()
    {
        Assert.Equal(RecapSentences.For(Unknown), RecapSentences.For("something_new"));
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void No_sentence_uses_technical_words(string type)
    {
        var sentence = RecapSentences.For(type).ToLowerInvariant();

        AssertPlain(sentence);
        Assert.DoesNotContain("service", sentence);
    }

    [Fact]
    public void No_management_text_uses_technical_words()
    {
        foreach (var reason in Reasons.Append("brand_new"))
            AssertPlain(RecapText.Gap(reason, "pilotage"));
        AssertPlain(RecapText.Incomplete([.. Reasons], "pilotage"));
        AssertPlain(RecapText.Notes([.. Reasons], 0.5, "pilotage"));
        AssertPlain(RecapText.Co2(2000, 3, null, "pilotage"));
        AssertPlain(RecapText.Co2(null, 0, "no_coefficient", "pilotage"));
        AssertPlain(RecapText.Co2(null, 0, "no_avoidable_io", "pilotage"));
        foreach (var state in new[] { "confirmed", "contradicted", "came_back", "awaiting" })
            AssertPlain(RecapText.Confirmation(state, "pilotage"));
        AssertPlain(RecapText.Truncated("pilotage"));
        AssertPlain(RecapText.SourcesWithoutVolume(["svc-quiet"], "pilotage"));
        AssertPlain(RecapText.LateSources(["svc-quiet"]));
        foreach (var (term, text) in RecapText.Help("pilotage"))
            AssertPlain(term + " " + text);
    }

    /// <summary>The Management ban list in French, beside the English one. "service" is in it: no French sentence names one.</summary>
    internal static readonly string[] FrenchTechnicalWords =
    [
        "requête", "base de données", "endpoint", "broker", "pool", "connexion", "sql", "http", "i/o", "e/s", "finding",
        "signature", "moteur", "daemon", "démon", "registre", "coefficient", "trace", "ingestion", "échantillon",
        "source", "service"
    ];

    internal static void AssertPlainFrench(string text)
    {
        foreach (var word in FrenchTechnicalWords)
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void No_french_sentence_uses_technical_words(string type)
    {
        var sentence = RecapSentences.For(type, HubLang.Fr);

        AssertPlainFrench(sentence);
        Assert.NotEqual(RecapSentences.For(type), sentence);
    }

    [Fact]
    public void No_french_management_text_uses_technical_words()
    {
        const string fr = HubLang.Fr;
        foreach (var reason in Reasons.Append("brand_new"))
            AssertPlainFrench(RecapText.Gap(reason, "pilotage", fr));
        AssertPlainFrench(RecapText.Incomplete([.. Reasons], "pilotage", fr));
        AssertPlainFrench(RecapText.Notes([.. Reasons], 0.5, "pilotage", fr));
        AssertPlainFrench(RecapText.Co2(2000, 3, null, "pilotage", fr));
        AssertPlainFrench(RecapText.Co2(null, 0, "no_coefficient", "pilotage", fr));
        AssertPlainFrench(RecapText.Co2(null, 0, "no_avoidable_io", "pilotage", fr));
        foreach (var state in new[] { "confirmed", "contradicted", "came_back", "awaiting" })
            AssertPlainFrench(RecapText.Confirmation(state, "pilotage", fr));
        AssertPlainFrench(RecapText.Truncated("pilotage", fr));
        AssertPlainFrench(RecapText.SourcesWithoutVolume(["svc-quiet"], "pilotage", fr));
        AssertPlainFrench(RecapText.LateSources(["svc-quiet"], fr));
        foreach (var (term, text) in RecapText.Help("pilotage", fr))
            AssertPlainFrench(term + " " + text);
    }
}
