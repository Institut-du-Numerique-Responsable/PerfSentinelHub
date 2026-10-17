using System.Text;
using System.Text.Json;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests.Collection;

public sealed class IncidentParserTests
{
    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "daemon-incidents-0.20.0.json");

    [Fact]
    public async Task The_capture_parses_into_columns_and_splits_the_findings_out()
    {
        var payload = await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken);

        var page = IncidentParser.Parse(payload);

        Assert.Equal(0, page.RejectedCount);
        var incident = Assert.Single(page.Incidents);
        Assert.Equal("d650edad80ac5c2d99b8d1dde07100c2", incident.Id);
        Assert.Equal("shop-svc", incident.Service);
        Assert.Equal("oom_kill", incident.Kind);
        Assert.Null(incident.EndedAtMs);
        Assert.Equal(incident.AtMs - 300_000, incident.WindowFromMs);
        Assert.True(incident.WindowToMs > incident.AtMs);
        Assert.NotNull(incident.OldestFindingMs);
        Assert.Equal(2, incident.FindingCount);
        // The document keeps every other daemon field and loses only the array.
        using var document = JsonDocument.Parse(incident.IncidentJson);
        Assert.False(document.RootElement.TryGetProperty("findings", out _));
        Assert.Equal("container memory limit reached", document.RootElement.GetProperty("detail").GetString());
        using var findings = JsonDocument.Parse(incident.FindingsJson);
        Assert.Equal(2, findings.RootElement.GetArrayLength());
    }

    [Theory]
    [InlineData("\"id\":\"d650edad80ac5c2d99b8d1dde07100c2\"", "\"id\":\"D650EDAD80AC5C2D99B8D1DDE07100C2\"")]
    [InlineData("\"id\":\"d650edad80ac5c2d99b8d1dde07100c2\"", "\"id\":\"d650edad\"")]
    [InlineData("\"service\":\"shop-svc\"", "\"service\":\"\"")]
    [InlineData("\"at_ms\":", "\"at_ms\":1,\"was_at_ms\":")]
    [InlineData("\"findings\":", "\"finding_rows\":")]
    public async Task A_malformed_incident_is_rejected_and_counted(string original, string replacement)
    {
        var template = await TemplateAsync();
        var broken = template.Replace(original, replacement, StringComparison.Ordinal);
        Assert.NotEqual(template, broken);
        var payload = Encoding.UTF8.GetBytes($"[{template},{broken}]");

        var page = IncidentParser.Parse(payload);

        Assert.Single(page.Incidents);
        Assert.Equal(1, page.RejectedCount);
    }

    [Fact]
    public async Task An_unknown_kind_folds_to_other()
    {
        var template = await TemplateAsync();
        var payload = Encoding.UTF8.GetBytes(
            $"[{template.Replace("\"kind\":\"oom_kill\"", "\"kind\":\"eclipse\"", StringComparison.Ordinal)}]");

        var incident = Assert.Single(IncidentParser.Parse(payload).Incidents);

        Assert.Equal("other", incident.Kind);
        Assert.Contains("other", IncidentParser.Kinds);
    }

    // SQLite reads the first of two properties of one name and System.Text.Json the last.
    [Fact]
    public async Task A_body_that_repeats_a_property_name_is_refused()
    {
        var template = await TemplateAsync();
        var repeated = template.Replace(
            "\"service\":\"shop-svc\"", "\"service\":\"shop-svc\",\"service\":\"other-svc\"", StringComparison.Ordinal);
        Assert.NotEqual(template, repeated);

        Assert.Throws<InvalidDataException>(() => IncidentParser.Parse(Encoding.UTF8.GetBytes($"[{repeated}]")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void A_body_that_is_not_an_array_is_invalid(string body)
    {
        Assert.Throws<InvalidDataException>(() => IncidentParser.Parse(Encoding.UTF8.GetBytes(body)));
    }

    private static async Task<string> TemplateAsync()
    {
        using var fixture = JsonDocument.Parse(
            await File.ReadAllBytesAsync(FixturePath, TestContext.Current.CancellationToken));
        return fixture.RootElement[0].GetRawText();
    }

    private static byte[] Envelope(string incidents, string producerVersion = "0.29.0")
    {
        return Encoding.UTF8.GetBytes($"{{\"producer_version\":\"{producerVersion}\",\"incidents\":{incidents}}}");
    }

    private static string FixtureIncidents()
    {
        return File.ReadAllText(FixturePath);
    }

    [Fact]
    public void An_import_envelope_parses_its_incidents_as_the_poll_does()
    {
        var import = IncidentParser.ParseImport(Envelope(FixtureIncidents()));

        Assert.Equal("0.29.0", import.ProducerVersion);
        var incident = Assert.Single(import.Page.Incidents);
        Assert.Equal("d650edad80ac5c2d99b8d1dde07100c2", incident.Id);
        Assert.Equal(2, incident.FindingCount);
        Assert.Equal(0, import.Page.RejectedCount);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    public void An_import_without_incidents_is_invalid(string incidents)
    {
        Assert.Throws<InvalidDataException>(() => IncidentParser.ParseImport(Envelope(incidents)));
    }

    [Fact]
    public void An_import_refuses_a_missing_or_overlong_producer_version()
    {
        Assert.Throws<InvalidDataException>(() => IncidentParser.ParseImport(
            Encoding.UTF8.GetBytes($"{{\"incidents\":{FixtureIncidents()}}}")));
        Assert.Throws<InvalidDataException>(() => IncidentParser.ParseImport(
            Envelope(FixtureIncidents(), new string('9', 65))));
    }

    [Fact]
    public void An_import_of_more_than_ten_incidents_is_too_large()
    {
        var one = FixtureIncidents().Trim()[1..^1];
        var eleven = "[" + string.Join(",", Enumerable.Repeat(one, 11)) + "]";

        Assert.Throws<ImportBatchTooLargeException>(() => IncidentParser.ParseImport(Envelope(eleven)));
    }

    [Fact]
    public void An_import_repeating_a_property_is_invalid()
    {
        var payload = Encoding.UTF8.GetBytes(
            $"{{\"producer_version\":\"0.29.0\",\"producer_version\":\"0.28.0\",\"incidents\":{FixtureIncidents()}}}");

        Assert.Throws<InvalidDataException>(() => IncidentParser.ParseImport(payload));
    }
}
