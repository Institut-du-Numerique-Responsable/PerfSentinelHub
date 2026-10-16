using System.Text;
using System.Text.Json.Nodes;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class CorrelationParserTests
{
    private static ParsedEpisodeImport Parse(params JsonObject[] episodes)
    {
        return CorrelationParser.ParseImport(Encoding.UTF8.GetBytes(EpisodeJson.Body(episodes)));
    }

    [Fact]
    public void An_episode_parses_with_both_sides_and_its_figures()
    {
        var import = Parse(EpisodeJson.Episode(EpisodeJson.Id(1)));

        Assert.Equal("0.30.0", import.ProducerVersion);
        Assert.Equal(0, import.RejectedCount);
        var episode = Assert.Single(import.Episodes);
        Assert.Equal(EpisodeJson.Id(1), episode.Id);
        Assert.Equal(EpisodeJson.PairKey, episode.PairKey);
        Assert.Equal(new EpisodeSide("n_plus_one_sql", "order-svc", "SELECT * FROM orders WHERE id = ?", null, null),
            episode.Source);
        Assert.Equal("pool_saturation", episode.Target.FindingType);
        Assert.Equal(1_800_000_000_000, episode.StartedAtMs);
        Assert.Null(episode.EndedAtMs);
        Assert.Equal(600_000, episode.GapMs);
        Assert.Equal(0.92, episode.PeakConfidence);
        Assert.Equal(41, episode.PeakCoOccurrences);
        Assert.Equal(1200.0, episode.MedianLagMs);
        Assert.Equal("00f067aa0ba902b74bf92f3577b34da6", episode.SourceSampleTraceId);
    }

    [Fact]
    public void A_side_keeps_its_grouping_key_and_value()
    {
        var grouped = EpisodeJson.Episode(EpisodeJson.Id(1));
        grouped["target"]!["grouping_key"] = "k8s.namespace.name";
        grouped["target"]!["grouping_value"] = "payments";

        var episode = Assert.Single(Parse(grouped).Episodes);

        Assert.Equal("k8s.namespace.name", episode.Target.GroupingKey);
        Assert.Equal("payments", episode.Target.GroupingValue);
        Assert.Null(episode.Source.GroupingValue);
    }

    [Fact]
    public void An_absent_end_reads_as_an_open_episode()
    {
        var open = EpisodeJson.Episode(EpisodeJson.Id(1));
        open.Remove("ended_at_ms");

        Assert.Null(Assert.Single(Parse(open).Episodes).EndedAtMs);
    }

    [Theory]
    [InlineData("id", "\"not-a-hex-id\"")]
    [InlineData("id", "\"0123456789ABCDEF0123456789ABCDEF\"")]
    [InlineData("pair_key", "\"0123\"")]
    [InlineData("started_at_ms", "1800000070000")]
    [InlineData("ended_at_ms", "1799999999999")]
    [InlineData("peak_confidence", "1.01")]
    [InlineData("last_confidence", "-0.1")]
    [InlineData("peak_co_occurrences", "-1")]
    [InlineData("last_co_occurrences", "\"37\"")]
    [InlineData("gap_ms", "0")]
    [InlineData("source", "\"order-svc\"")]
    public void An_invalid_episode_is_rejected_alone(string field, string json)
    {
        var invalid = EpisodeJson.Episode(EpisodeJson.Id(2));
        invalid[field] = JsonNode.Parse(json);

        var import = Parse(EpisodeJson.Episode(EpisodeJson.Id(1)), invalid);

        Assert.Equal(EpisodeJson.Id(1), Assert.Single(import.Episodes).Id);
        Assert.Equal(1, import.RejectedCount);
    }

    [Theory]
    [InlineData("source", "finding_type", "made_up")]
    [InlineData("target", "finding_type", "made_up")]
    [InlineData("target", "service", "")]
    public void A_side_the_engine_cannot_have_written_rejects_its_episode(string side, string field, string value)
    {
        var invalid = EpisodeJson.Episode(EpisodeJson.Id(2));
        invalid[side]![field] = value;

        Assert.Equal(1, Parse(EpisodeJson.Episode(EpisodeJson.Id(1)), invalid).RejectedCount);
    }

    [Fact]
    public void An_overlong_sample_trace_rejects_its_episode()
    {
        var invalid = EpisodeJson.Episode(EpisodeJson.Id(2));
        invalid["sample_trace_id"] = new string('a', CorrelationParser.MaxTraceIdLength + 1);

        Assert.Equal(1, Parse(EpisodeJson.Episode(EpisodeJson.Id(1)), invalid).RejectedCount);
    }

    [Fact]
    public void A_missing_sample_trace_is_kept_as_unknown()
    {
        var episode = EpisodeJson.Episode(EpisodeJson.Id(1));
        episode["sample_trace_id"] = null;

        Assert.Null(Assert.Single(Parse(episode).Episodes).SampleTraceId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public void An_import_without_episodes_is_invalid(string episodes)
    {
        Assert.Throws<InvalidDataException>(() => CorrelationParser.ParseImport(
            Encoding.UTF8.GetBytes($"{{\"producer_version\":\"0.30.0\",\"episodes\":{episodes}}}")));
    }

    [Fact]
    public void An_import_refuses_a_missing_or_overlong_producer_version()
    {
        var episodes = $"[{EpisodeJson.Episode(EpisodeJson.Id(1)).ToJsonString()}]";
        Assert.Throws<InvalidDataException>(() => CorrelationParser.ParseImport(
            Encoding.UTF8.GetBytes($"{{\"episodes\":{episodes}}}")));
        Assert.Throws<InvalidDataException>(() => CorrelationParser.ParseImport(
            Encoding.UTF8.GetBytes($"{{\"producer_version\":\"{new string('9', 65)}\",\"episodes\":{episodes}}}")));
    }

    [Fact]
    public void An_import_of_more_than_a_hundred_episodes_is_too_large()
    {
        var episodes = Enumerable.Range(1, CorrelationParser.MaxImportEpisodes + 1)
            .Select(index => EpisodeJson.Episode(EpisodeJson.Id(index)))
            .ToArray();

        Assert.Throws<ImportBatchTooLargeException>(() => Parse(episodes));
    }

    [Fact]
    public void An_import_repeating_a_property_is_invalid()
    {
        var episodes = $"[{EpisodeJson.Episode(EpisodeJson.Id(1)).ToJsonString()}]";
        var payload = Encoding.UTF8.GetBytes(
            $"{{\"producer_version\":\"0.30.0\",\"producer_version\":\"0.29.0\",\"episodes\":{episodes}}}");

        Assert.Throws<InvalidDataException>(() => CorrelationParser.ParseImport(payload));
    }

    [Fact]
    public void An_import_that_is_not_json_is_invalid()
    {
        Assert.Throws<InvalidDataException>(() =>
            CorrelationParser.ParseImport(Encoding.UTF8.GetBytes("{\"episodes\":")));
    }
}
