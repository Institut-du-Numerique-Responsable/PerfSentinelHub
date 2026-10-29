using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class ActionConfirmationTests
{
    private const int Done = 100;

    [Fact]
    public void The_done_day_is_ignored()
    {
        Assert.Equal("confirmed", ActionConfirmation.Judge(Done, [Done], [Done + 1]));
    }

    [Fact]
    public void A_later_detection_contradicts()
    {
        Assert.Equal("contradicted", ActionConfirmation.Judge(Done, [Done + 1], []));
    }

    [Fact]
    public void Clean_then_detected_came_back()
    {
        Assert.Equal("came_back", ActionConfirmation.Judge(Done, [Done + 2], [Done + 1]));
    }

    [Fact]
    public void Nothing_sealed_yet_is_awaiting()
    {
        Assert.Equal("awaiting", ActionConfirmation.Judge(Done, [Done], [Done]));
    }
}
