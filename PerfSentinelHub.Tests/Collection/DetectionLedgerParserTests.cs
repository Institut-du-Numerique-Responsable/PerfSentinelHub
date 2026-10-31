using System.Text;
using System.Text.Json.Nodes;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Collection;

public sealed class DetectionLedgerParserTests
{
    // Far enough from the epoch that the window's lower edge is a real day, not a negative one.
    private const int Today = 20_400;
    private static readonly long NowMs = DetectionLedgerJson.Midnight(Today) + 3_600_000;

    private static JsonObject Valid(params JsonObject[] counts)
    {
        return DetectionLedgerJson.Page(
            counts.Length == 0 ? [DetectionLedgerJson.Count(Today)] : counts,
            [DetectionLedgerJson.Day(Today)],
            Today);
    }

    private static DetectionLedgerPage Parse(JsonObject page)
    {
        return DetectionLedgerJson.Parse(page, NowMs);
    }

    [Fact]
    public void A_page_parses_with_its_header_rows_and_days()
    {
        var page = Parse(Valid());

        Assert.Equal(DetectionLedgerJson.LedgerId, page.LedgerId);
        Assert.Equal(DetectionLedgerJson.Midnight(Today - 2) + 1_000, page.StartedMs);
        Assert.Equal(Today, page.CurrentDay);
        Assert.Equal(Today - 2, page.OldestDay);
        Assert.Equal(Today - 1, page.SealedThrough);
        Assert.Equal(new DetectionCountRow(Today, DetectionLedgerJson.Signature, 5, 40), Assert.Single(page.Counts));
        Assert.Equal(new DetectionDayRow(Today, false, false, 0, 0, 0, 0, 1.0), Assert.Single(page.Days));
        Assert.Null(page.NextAfter);
    }

    [Fact]
    public void A_cursor_and_an_unsealed_ledger_read_as_given()
    {
        var json = Valid();
        json["next_after"] = $"{Today}:{DetectionLedgerJson.Signature}";
        json["sealed_through"] = null;

        var page = Parse(json);

        Assert.Equal($"{Today}:{DetectionLedgerJson.Signature}", page.NextAfter);
        Assert.Null(page.SealedThrough);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000000040008000000000000001")]
    public void A_non_guid_ledger_is_refused(string ledgerId)
    {
        var json = Valid();
        json["ledger_id"] = ledgerId;

        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void An_upper_case_ledger_id_reads_as_the_lower_case_one()
    {
        var json = Valid();
        json["ledger_id"] = "0000000A-0000-4000-8000-00000000000B";

        Assert.Equal("0000000a-0000-4000-8000-00000000000b", Parse(json).LedgerId);
    }

    [Theory]
    [InlineData(Today - 401)]
    [InlineData(Today + 3)]
    public void A_day_outside_the_window_is_refused(int day)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Valid(DetectionLedgerJson.Count(day))));
        var json = Valid();
        json["days"] = new JsonArray(DetectionLedgerJson.Day(day));
        Assert.Throws<InvalidDataException>(() => Parse(json));
        json = Valid();
        json["sealed_through"] = day;
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Theory]
    [InlineData(Today - 400)]
    [InlineData(Today + 2)]
    public void A_day_on_the_window_edge_is_accepted(int day)
    {
        Assert.Equal(day, Assert.Single(Parse(Valid(DetectionLedgerJson.Count(day))).Counts).Day);
    }

    [Fact]
    public void A_1025_char_signature_is_refused()
    {
        Assert.Single(Parse(Valid(DetectionLedgerJson.Count(Today, new string('s', 1024)))).Counts);
        Assert.Throws<InvalidDataException>(() =>
            Parse(Valid(DetectionLedgerJson.Count(Today, new string('s', 1025)))));
    }

    [Fact]
    public void An_empty_signature_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Parse(Valid(DetectionLedgerJson.Count(Today, ""))));
    }

    [Theory]
    [InlineData("n_plus_one_sql:order-svc:SELECT 1\n")]
    [InlineData("n_plus_one_sql:\u001b[31morder-svc")]
    public void A_control_char_in_a_signature_is_refused(string signature)
    {
        Assert.Throws<InvalidDataException>(() => Parse(Valid(DetectionLedgerJson.Count(Today, signature))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9_007_199_254_740_993)]
    public void A_negative_or_2_pow_53_plus_one_count_is_refused(long value)
    {
        Assert.Throws<InvalidDataException>(() =>
            Parse(Valid(DetectionLedgerJson.Count(Today, detections: value))));
        Assert.Throws<InvalidDataException>(() =>
            Parse(Valid(DetectionLedgerJson.Count(Today, extraIoOps: value))));
        var json = Valid();
        json["days"]![0]!["shed_traces"] = value;
        Assert.Throws<InvalidDataException>(() => Parse(json));
        json = Valid();
        json["started_ms"] = value;
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void A_count_of_2_pow_53_is_accepted()
    {
        var row = Assert.Single(Parse(Valid(DetectionLedgerJson.Count(Today, detections: 9_007_199_254_740_992)))
            .Counts);

        Assert.Equal(9_007_199_254_740_992, row.Detections);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("\"5\"")]
    [InlineData("null")]
    public void A_count_that_is_not_an_integer_is_refused(string literal)
    {
        var count = DetectionLedgerJson.Count(Today);
        count["detections"] = JsonNode.Parse(literal);

        Assert.Throws<InvalidDataException>(() => Parse(Valid(count)));
    }

    [Fact]
    public void A_sampling_rate_of_zero_is_accepted_and_above_one_refused()
    {
        var json = Valid();
        json["days"] = new JsonArray(DetectionLedgerJson.Day(Today, samplingRate: 0));
        Assert.Equal(0, Assert.Single(Parse(json).Days).SamplingRate);

        json["days"] = new JsonArray(DetectionLedgerJson.Day(Today, samplingRate: 1.000001));
        Assert.Throws<InvalidDataException>(() => Parse(json));
        json["days"] = new JsonArray(DetectionLedgerJson.Day(Today, samplingRate: -0.1));
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void Over_5000_rows_is_too_large()
    {
        var rows = Enumerable.Range(0, 5000).Select(index => DetectionLedgerJson.Count(Today, $"sig-{index}"))
            .ToArray();
        Assert.Equal(5000, Parse(Valid(rows)).Counts.Count);

        Assert.Throws<ImportBatchTooLargeException>(() =>
            Parse(Valid([.. rows, DetectionLedgerJson.Count(Today, "sig-5000")])));
    }

    [Fact]
    public void Over_31_days_is_too_large()
    {
        var json = Valid();
        json["days"] = new JsonArray([
            .. Enumerable.Range(0, 31).Select(JsonNode? (offset) => DetectionLedgerJson.Day(Today - offset))
        ]);
        Assert.Equal(31, Parse(json).Days.Count);

        json["days"]!.AsArray().Add(DetectionLedgerJson.Day(Today - 31));
        Assert.Throws<ImportBatchTooLargeException>(() => Parse(json));
    }

    [Fact]
    public void Absent_extra_io_reads_as_null()
    {
        var json = Valid(DetectionLedgerJson.Count(Today, extraIoOps: null));
        json["days"]![0]!.AsObject().Remove("overflow_extra_io_ops");

        var page = Parse(json);

        Assert.Null(Assert.Single(page.Counts).ExtraIoOps);
        Assert.Null(Assert.Single(page.Days).OverflowExtraIoOps);
    }

    [Fact]
    public void An_empty_counts_page_with_a_sealed_day_is_valid()
    {
        var json = DetectionLedgerJson.Page([], [DetectionLedgerJson.Day(Today - 1, true)], Today);

        var page = Parse(json);

        Assert.Empty(page.Counts);
        Assert.Equal(new DetectionDayRow(Today - 1, true, false, 0, 0, 0, 0, 1.0), Assert.Single(page.Days));
    }

    [Theory]
    [InlineData("counts")]
    [InlineData("days")]
    [InlineData("ledger_id")]
    [InlineData("current_day")]
    public void A_missing_member_is_refused(string member)
    {
        var json = Valid();
        json.Remove(member);

        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Fact]
    public void A_day_flag_that_is_not_a_boolean_is_refused()
    {
        var json = Valid();
        json["days"]![0]!["sealed"] = 1;

        Assert.Throws<InvalidDataException>(() => Parse(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData(
        "{\"ledger_id\":\"00000000-0000-4000-8000-000000000001\",\"ledger_id\":\"00000000-0000-4000-8000-000000000002\"}")]
    public void A_body_that_is_not_one_page_object_is_refused(string body)
    {
        Assert.Throws<InvalidDataException>(() =>
            DetectionLedgerParser.ParsePage(Encoding.UTF8.GetBytes(body), NowMs));
    }

    [Fact]
    public void Absent_carbon_fields_read_as_null()
    {
        var day = Assert.Single(Parse(Valid()).Days);

        Assert.Null(day.AvoidableGco2);
        Assert.Null(day.AvoidableIoOps);
        Assert.Null(day.AvoidableGco2Low);
        Assert.Null(day.TotalIoOps);
    }

    [Fact]
    public void Carbon_fields_are_read()
    {
        var json = Valid();
        json["days"] = new JsonArray(DetectionLedgerJson.Day(
            Today, avoidableGco2: 12.5, avoidableIoOps: 400, avoidableGco2Low: 6.25, totalIoOps: 5200));

        var day = Assert.Single(Parse(json).Days);

        Assert.Equal(12.5, day.AvoidableGco2);
        Assert.Equal(400, day.AvoidableIoOps);
        Assert.Equal(6.25, day.AvoidableGco2Low);
        Assert.Equal(5200, day.TotalIoOps);
    }

    [Theory]
    [InlineData("avoidable_gco2", "-0.5")]
    [InlineData("avoidable_gco2", "\"12\"")]
    [InlineData("avoidable_gco2", "9007199254740994")]
    [InlineData("avoidable_gco2_low", "-0.5")]
    [InlineData("avoidable_gco2_low", "\"12\"")]
    [InlineData("avoidable_io_ops", "-1")]
    [InlineData("total_io_ops", "-1")]
    public void A_carbon_field_out_of_range_is_refused(string field, string literal)
    {
        var json = Valid();
        var day = DetectionLedgerJson.Day(Today);
        day[field] = JsonNode.Parse(literal);
        json["days"] = new JsonArray(day);

        var error = Assert.Throws<InvalidDataException>(() => Parse(json));

        Assert.Contains(field, error.Message);
    }

    [Theory]
    [InlineData("avoidable_gco2")]
    [InlineData("avoidable_gco2_low")]
    public void A_null_carbon_number_reads_as_null(string field)
    {
        var json = Valid();
        var day = DetectionLedgerJson.Day(Today);
        day[field] = null;
        json["days"] = new JsonArray(day);

        var read = Assert.Single(Parse(json).Days);

        Assert.Null(read.AvoidableGco2);
        Assert.Null(read.AvoidableGco2Low);
    }

    private static JsonObject AllThresholds()
    {
        return new JsonObject
        {
            ["slow_query_threshold_ms"] = 500,
            ["slow_http_threshold_ms"] = 1000,
            ["max_fanout"] = 20,
            ["chatty_threshold"] = 15,
            ["pool_saturation_threshold"] = 10,
            ["serialized_min_calls"] = 3
        };
    }

    private static JsonObject WithThresholds(JsonNode? thresholds)
    {
        var day = DetectionLedgerJson.Day(Today);
        day["thresholds"] = thresholds;
        var json = Valid();
        json["days"] = new JsonArray(day);
        return json;
    }

    [Fact]
    public void Below_threshold_passes_is_read()
    {
        var row = Assert.Single(Parse(Valid(DetectionLedgerJson.Count(Today, belowThresholdPasses: 12))).Counts);

        Assert.Equal(12, row.BelowThresholdPasses);
    }

    [Fact]
    public void Absent_below_threshold_passes_reads_as_null()
    {
        Assert.Null(Assert.Single(Parse(Valid()).Counts).BelowThresholdPasses);
    }

    [Fact]
    public void A_negative_below_threshold_passes_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Parse(Valid(DetectionLedgerJson.Count(Today, belowThresholdPasses: -1))));

        Assert.Contains("below_threshold_passes", error.Message);
    }

    [Fact]
    public void Thresholds_are_read()
    {
        var day = Assert.Single(Parse(WithThresholds(AllThresholds())).Days);

        Assert.Equal(new DetectionThresholds(500, 1000, 20, 15, 10, 3), day.Thresholds);
    }

    [Fact]
    public void Absent_or_null_thresholds_read_as_null()
    {
        Assert.Null(Assert.Single(Parse(Valid()).Days).Thresholds);
        Assert.Null(Assert.Single(Parse(WithThresholds(null)).Days).Thresholds);
    }

    [Fact]
    public void A_thresholds_value_that_is_not_an_object_is_refused()
    {
        var error = Assert.Throws<InvalidDataException>(() => Parse(WithThresholds(5)));

        Assert.Equal("thresholds is not an object.", error.Message);
    }

    [Fact]
    public void A_string_threshold_is_refused()
    {
        var thresholds = AllThresholds();
        thresholds["max_fanout"] = "20";

        var error = Assert.Throws<InvalidDataException>(() => Parse(WithThresholds(thresholds)));

        Assert.Equal("max_fanout is not an integer in [0, 2^53].", error.Message);
    }

    [Fact]
    public void An_unknown_threshold_key_is_ignored()
    {
        var thresholds = new JsonObject { ["max_fanout"] = 20, ["a_later_threshold"] = "anything" };

        var day = Assert.Single(Parse(WithThresholds(thresholds)).Days);

        Assert.Equal(new DetectionThresholds(null, null, 20, null, null, null), day.Thresholds);
    }

    [Fact]
    public void A_count_of_zero_detections_is_accepted()
    {
        // A watched signature seen only below its threshold that day.
        var row = Assert.Single(Parse(Valid(DetectionLedgerJson.Count(Today, detections: 0))).Counts);

        Assert.Equal(0, row.Detections);
    }
}
