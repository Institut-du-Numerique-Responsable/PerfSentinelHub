using PerfSentinelHub.Cards;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Cards;

public sealed class CardTypesTests
{
    [Fact]
    public void Every_finding_type_has_a_family()
    {
        string[] families = ["extra_calls", "slow_calls", "call_architecture"];

        Assert.All(FindingLabels.Types, type => Assert.Contains(CardTypes.Family(type), families));
        Assert.Equal("other", CardTypes.Family("unknown_type"));
    }

    // Distinct values 1 to 6, so a type read from the wrong key shows.
    [Theory]
    [InlineData("slow_sql", 1L)]
    [InlineData("slow_messaging", 1L)]
    [InlineData("slow_http", 2L)]
    [InlineData("excessive_fanout", 3L)]
    [InlineData("chatty_service", 4L)]
    [InlineData("pool_saturation", 5L)]
    [InlineData("serialized_calls", 6L)]
    [InlineData("redundant_sql", null)]
    public void Threshold_keys_follow_the_contract(string findingType, long? expected)
    {
        var thresholds = new DetectionThresholds(1, 2, 3, 4, 5, 6);

        Assert.Equal(expected, CardTypes.ThresholdOf(thresholds, findingType));
        Assert.Equal(expected is not null, CardTypes.ThresholdTypes.Contains(findingType));
    }

    [Fact]
    public void Only_n_plus_one_and_redundant_have_avoidable_volume()
    {
        Assert.Equal(
            ["n_plus_one_sql", "n_plus_one_http", "n_plus_one_messaging", "redundant_sql", "redundant_http"],
            FindingLabels.Types.Where(CardTypes.HasAvoidableVolume));
    }

    [Fact]
    public void TypeOf_reads_the_prefix()
    {
        Assert.Equal("slow_sql", CardTypes.TypeOf("slow_sql:svc:GET_/a:ab"));
        Assert.Equal("nocolon", CardTypes.TypeOf("nocolon"));
    }
}
