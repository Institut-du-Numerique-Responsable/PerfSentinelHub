using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapSentencesTests
{
    private const string Unknown = "blocking_wait";

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

        foreach (var word in new[]
                 { "query", "database", "service", "endpoint", "broker", "pool", "connection", "sql", "http" })
            Assert.DoesNotContain(word, sentence);
    }
}
