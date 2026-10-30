using PerfSentinelHub.Cards;

namespace PerfSentinelHub.Tests.Cards;

public sealed class CardEnvelopeTests
{
    [Fact]
    public void Reads_the_figures_a_story_cites()
    {
        var frozen = CardEnvelope.Read("""
                                       {"finding":{"type":"slow_sql","service":"orders","source_endpoint":"GET /orders",
                                         "pattern":{"occurrences":41,"span_duration_us_p99":2345678},
                                         "first_timestamp":"2026-10-12T08:30:00Z"}}
                                       """);

        Assert.Equal(new FrozenFinding("slow_sql", "orders", "GET /orders", 41, 2_345_678, 1_791_793_800_000), frozen);
    }

    [Fact]
    public void A_missing_pattern_reads_null_figures()
    {
        var frozen = CardEnvelope.Read("""{"finding":{"type":"n_plus_one_sql"}}""");

        Assert.Equal(new FrozenFinding("n_plus_one_sql", "", "", null, null, null), frozen);
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("12")]
    public void An_unreadable_first_timestamp_reads_null(string timestamp)
    {
        var frozen = CardEnvelope.Read("""{"finding":{"type":"slow_sql","first_timestamp":""" + timestamp + "}}");

        Assert.Null(frozen.ObservedAtMs);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    public void An_unreadable_envelope_reads_empty(string json)
    {
        Assert.Equal(new FrozenFinding("", "", "", null, null, null), CardEnvelope.Read(json));
    }
}
