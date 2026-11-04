namespace PerfSentinelHub.Recaps;

/// <summary>The recap stages as the wire names them, in the order a computation takes them.</summary>
public static class RecapStages
{
    public static readonly IReadOnlyList<RecapStage> All = Enum.GetValues<RecapStage>();

    public static string Name(RecapStage stage)
    {
        return stage switch
        {
            RecapStage.FindingsPeriod => "findings_period",
            RecapStage.FindingsOpen => "findings_open",
            RecapStage.FindingsClosed => "findings_closed",
            RecapStage.FindingsPrevious => "findings_previous",
            RecapStage.Coverage => "coverage",
            RecapStage.Carbon => "carbon",
            RecapStage.Actions => "actions",
            RecapStage.Labels => "labels",
            RecapStage.Assemble => "assemble",
            // No catch-all: a stage added without a name must fail rather than reach the wire unnamed.
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, null)
        };
    }
}
