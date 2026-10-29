using System.Text.Json;
using System.Text.Json.Nodes;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Recaps;

public sealed class RecapEnvelopeTests
{
    private static StoredFinding Stored(string envelope, DetectionVolume? volume = null)
    {
        return new StoredFinding("sig", envelope, 1, 2, "daemon_production", "open",
            [new FindingSourceObservation("src-a", "A", "prod", null, null, null)])
        {
            Volume = volume
        };
    }

    [Fact]
    public void Read_takes_the_suggestion_fix_and_code_location()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "daemon-findings-0.11.2.json");
        var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsArray()[0]!;
        envelope["finding"]!["suggested_fix"] = JsonNode.Parse(
            """{"recommendation":"Batch it","framework":"efcore","reference_url":"https://docs.example/batch"}""");

        var found = RecapEnvelope.Read(Stored(
            envelope.ToJsonString(), new DetectionVolume(8, true, 120, [], 1.0)));

        Assert.Equal("sig", found.Signature);
        Assert.Equal("blocking_wait", found.FindingType);
        Assert.Equal("critical", found.Severity);
        Assert.Equal("rider-smoke", found.Service);
        Assert.Equal("POST /checkout", found.Endpoint);
        Assert.Equal("Thread.Sleep(25)", found.Template);
        Assert.Equal("Remove the blocking wait from the request path", found.Suggestion);
        Assert.Equal(new RecapFix("Batch it", "efcore", "https://docs.example/batch"), found.SuggestedFix);
        Assert.Equal(new RecapCodeLocation("SlowPath", "Program.cs", 18, "PerfSentinel.RiderSmoke.Program"),
            found.CodeLocation);
        Assert.Equal("daemon_production", found.Confidence);
        Assert.Equal(8, found.Detections);
        Assert.Equal(120, found.AvoidableIoOps);
        Assert.True(found.VolumeKnown);
        Assert.Equal(["src-a"], found.SourceIds);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"finding":[]}""")]
    [InlineData("""{"finding":{"type":3,"suggested_fix":{"framework":"x"},"code_location":"here"}}""")]
    public void A_bare_envelope_reads_without_throwing(string envelope)
    {
        var found = RecapEnvelope.Read(Stored(envelope));

        Assert.Equal("", found.Service);
        Assert.Equal("", found.FindingType);
        Assert.Null(found.Suggestion);
        Assert.Null(found.SuggestedFix);
        Assert.Null(found.CodeLocation);
        Assert.Null(found.Detections);
        Assert.False(found.VolumeKnown);
    }
}
