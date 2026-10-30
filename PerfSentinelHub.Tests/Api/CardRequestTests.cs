using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PerfSentinelHub.Api;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Api;

/// <summary>What a caller may ask of a share card, on Wednesday 2026-11-04: only the three free fields are free.</summary>
public sealed class CardRequestTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    [Theory]
    [InlineData("figures")]
    [InlineData("org")]
    [InlineData("scope")]
    [InlineData("generated_at_ms")]
    [InlineData("before_fix_period")]
    public void Only_title_team_and_label_are_free(string key)
    {
        var body = Body(("angle", "story"), ("period", "month"));
        body[key] = new JsonObject();

        Assert.Equal(
            "Only angle, period, at, environment, source_id, signature, title, team, label, lang and format may " +
            "be sent: the figures come from the Hub.",
            Refused(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("poster")]
    public void Angle_must_be_one_of_the_three(string? angle)
    {
        Assert.Equal("angle must be story, curve or collective.", Refused(Body(("angle", angle), ("period", "month"))));
    }

    [Theory]
    [InlineData("day")]
    [InlineData(null)]
    public void Period_must_be_a_card_kind(string? period)
    {
        Assert.Equal("period must be week, month, quarter, semester or year.",
            Refused(Body(("angle", "curve"), ("period", period))));
    }

    [Fact]
    public void A_period_not_yet_closed_is_refused()
    {
        Assert.Equal("A card covers a closed period. Pick an earlier one.",
            Refused(Body(("angle", "curve"), ("period", "month"), ("at", "2026-11-01"))));
        Assert.Equal("at must be a date, yyyy-MM-dd.",
            Refused(Body(("angle", "curve"), ("period", "month"), ("at", "1 November"))));
    }

    // The last day the calendar holds: the end of its period would overflow, which must be a 400.
    [Theory]
    [InlineData("week")]
    [InlineData("month")]
    [InlineData("quarter")]
    [InlineData("semester")]
    [InlineData("year")]
    public void A_date_beyond_today_is_refused_without_overflowing(string period)
    {
        Assert.Equal("A card covers a closed period. Pick an earlier one.",
            Refused(Body(("angle", "curve"), ("period", period), ("at", "9999-12-31"))));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString($"?period={period}&at=9999-12-31");
        Assert.False(CardRequest.TryParseOptionsQuery(context.Request, Today, out _));
    }

    [Fact]
    public void The_last_closed_period_is_the_default()
    {
        var request = Accepted(Body(("angle", "collective"), ("period", "quarter")));

        Assert.Equal((RecapPeriodKind.Quarter, new DateOnly(2026, 7, 1)), (request.Kind, request.At));
        Assert.Equal(("en", "png"), (request.Lang, request.Format));
        Assert.Equal(new CardFree(null, null, null), request.Free);
    }

    [Fact]
    public void A_label_or_signature_outside_a_story_is_refused()
    {
        Assert.Equal("signature and label belong to a story card.",
            Refused(Body(("angle", "curve"), ("period", "month"), ("label", "Order list"))));
        Assert.Equal("signature and label belong to a story card.",
            Refused(Body(("angle", "collective"), ("period", "month"), ("signature", "n_plus_one_sql:a:1"))));

        var story = Accepted(Body(("angle", "story"), ("period", "month"), ("signature", "n_plus_one_sql:a:1"),
            ("label", "Order list"), ("environment", "prod"), ("source_id", "a")));

        Assert.Equal(("n_plus_one_sql:a:1", "Order list"), (story.Signature, story.Free.Label));
        Assert.Equal(("prod", "a"), (story.Environment, story.SourceId));
    }

    [Fact]
    public void Free_fields_are_trimmed_and_bounded()
    {
        var blank = Accepted(Story(("title", "  "), ("team", " "), ("label", "")));
        Assert.Equal(new CardFree(null, null, null), blank.Free);

        var trimmed = Accepted(Story(("title", "  Our month "), ("team", " Checkout")));
        Assert.Equal(new CardFree("Our month", "Checkout", null), trimmed.Free);

        var longest = Accepted(Story(("title", new string('t', 48)), ("team", new string('m', 48)),
            ("label", new string('l', 80))));
        Assert.Equal((48, 48, 80), (longest.Free.Title!.Length, longest.Free.Team!.Length, longest.Free.Label!.Length));

        Assert.Equal("title must be 1 to 48 characters with no control or format character.",
            Refused(Story(("title", new string('t', 49)))));
        Assert.Equal("team must be 1 to 48 characters with no control or format character.",
            Refused(Story(("team", new string('m', 49)))));
        Assert.Equal("label must be 1 to 80 characters with no control or format character.",
            Refused(Story(("label", new string('l', 81)))));
        foreach (var field in new[] { "title", "team", "label" })
            foreach (var hidden in new[] { "a\u0007b", "a\u202Eb", "a\u200Bb" })
                Assert.StartsWith($"{field} must be", Refused(Story((field, hidden))), StringComparison.Ordinal);
        Assert.StartsWith("title must be", Refused(Story(("title", 3))), StringComparison.Ordinal);
    }

    [Fact]
    public void Lang_is_en_or_fr()
    {
        Assert.Equal("en", Accepted(Body(("angle", "curve"), ("period", "month"))).Lang);
        Assert.Equal("fr", Accepted(Body(("angle", "curve"), ("period", "month"), ("lang", "fr"))).Lang);
        Assert.Equal("lang must be en or fr.", Refused(Body(("angle", "curve"), ("period", "month"), ("lang", "de"))));
    }

    [Fact]
    public void Format_is_png_or_pdf()
    {
        Assert.Equal("pdf", Accepted(Body(("angle", "curve"), ("period", "month"), ("format", "pdf"))).Format);
        Assert.Equal("format must be png or pdf.",
            Refused(Body(("angle", "curve"), ("period", "month"), ("format", "svg"))));
    }

    [Fact]
    public void A_body_that_is_not_a_json_object_is_refused()
    {
        Assert.Equal("The body is not valid JSON.", Refused("{"u8.ToArray()));
        Assert.Equal("The body must be a JSON object.", Refused("[]"u8.ToArray()));
    }

    [Fact]
    public void The_options_query_reads_an_open_period_and_the_scope()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?period=month&at=2026-11-02&environment=prod&source_id=a");

        Assert.True(CardRequest.TryParseOptionsQuery(context.Request, Today, out var query));
        Assert.Equal((RecapPeriodKind.Month, new DateOnly(2026, 11, 2), "prod", "a"), query);

        context.Request.QueryString = new QueryString("?period=quarter");
        Assert.True(CardRequest.TryParseOptionsQuery(context.Request, Today, out query));
        Assert.Equal((RecapPeriodKind.Quarter, new DateOnly(2026, 7, 1), null, null), query);

        foreach (var invalid in new[] { "", "?period=day", "?period=month&at=soon", "?period=month&period=week" })
        {
            context.Request.QueryString = new QueryString(invalid);
            Assert.False(CardRequest.TryParseOptionsQuery(context.Request, Today, out _));
        }
    }

    private static JsonObject Story(params (string Key, object? Value)[] fields)
    {
        return Body([("angle", "story"), ("period", "month"), .. fields]);
    }

    private static JsonObject Body(params (string Key, object? Value)[] fields)
    {
        var body = new JsonObject();
        foreach (var (key, value) in fields.Where(field => field.Value is not null))
            body[key] = value is int number ? JsonValue.Create(number) : JsonValue.Create((string)value!);
        return body;
    }

    private static CardRequest Accepted(JsonObject body)
    {
        var request = CardRequest.TryParse(Encoding.UTF8.GetBytes(body.ToJsonString()), Today, out var error);
        Assert.Null(error);
        return Assert.IsType<CardRequest>(request);
    }

    private static string Refused(JsonObject body)
    {
        return Refused(Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    private static string Refused(byte[] body)
    {
        Assert.Null(CardRequest.TryParse(body, Today, out var error));
        return Assert.IsType<string>(error);
    }
}
