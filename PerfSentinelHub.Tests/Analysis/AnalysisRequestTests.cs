using System.Text.Json;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests.Analysis;

public sealed class AnalysisRequestTests
{
    private const long Now = 1_787_839_140_000;

    [Fact]
    public void A_daemon_snapshot_takes_no_parameters()
    {
        Assert.NotNull(Parse("{}", SourceKinds.Daemon, out var accepted));
        Assert.Null(accepted);

        Assert.Null(Parse("""{"service":"orders"}""", SourceKinds.Daemon, out var rejected));
        Assert.Equal(
            "A daemon snapshot takes no parameters, and detects with its own configuration.",
            rejected);
    }

    [Theory]
    // A trace ID resolves to exactly one trace, so no window applies to it.
    [InlineData("""{"trace_id":"abc123","lookback":"1h"}""")]
    [InlineData("""{"trace_id":"abc123","service":"orders"}""")]
    [InlineData("""{"trace_id":"abc123","from_ms":1,"to_ms":2}""")]
    // Mutually exclusive, the way the engine takes them.
    [InlineData("""{"service":"orders","lookback":"1h","from_ms":1,"to_ms":2}""")]
    // Half an absolute window is not a window.
    [InlineData("""{"service":"orders","from_ms":1787838000000}""")]
    // No implicit window: the engine's own 1h default would not be recorded.
    [InlineData("""{"service":"orders"}""")]
    [InlineData("""{"service":"orders","lookback":"1 hour"}""")]
    [InlineData("""{"service":"orders","lookback":"0h"}""")]
    [InlineData("""{"service":"","lookback":"1h"}""")]
    [InlineData("""{"trace_id":"not a trace id"}""")]
    public void An_impossible_pair_is_refused_before_it_reaches_the_engine(string payload)
    {
        Assert.Null(Parse(payload, SourceKinds.Tempo, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("""{"service":"orders","from_ms":1,"to_ms":2,"max_traces":50,"note":"x"}""",
        """{"service":"orders","from_ms":1,"to_ms":2,"max_traces":50}""")]
    [InlineData("""{"trace_id":"abc123","detection":{}}""", """{"trace_id":"abc123"}""")]
    // Every knob kind, a default included, the decimal at its shortest spelling.
    [InlineData(
        """{"service":"orders","lookback":"1h","detection":{"max_fanout":20,"sanitizer_aware_min_cv":0.7500000000000000000001,"sanitizer_aware_classification":"strict"}}""",
        """{"service":"orders","lookback":"1h","detection":{"max_fanout":20,"sanitizer_aware_min_cv":0.75,"sanitizer_aware_classification":"strict"}}""")]
    public void The_stored_form_holds_only_what_the_parser_read(string payload, string stored)
    {
        var request = Parse(payload, SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal(stored, request!.ToJson());
        // And reads back to the same request.
        Assert.Equal(stored, Parse(stored, SourceKinds.Tempo, out _)!.ToJson());
    }

    [Theory]
    // A service.name never needs them, and a trace backend's tag search reads them as syntax:
    // Tempo parses `service.name=<value>` as logfmt, where a space or a newline starts a second pair.
    [InlineData("orders-x service.name=payments")]
    [InlineData("orders-x\nservice.name=payments")]
    [InlineData("orders\tx")]
    [InlineData("orders\u00a0x")]
    [InlineData("orders=x")]
    [InlineData("orders\"x")]
    [InlineData("orders\\x")]
    [InlineData("orders\u0001")]
    public void A_service_name_carrying_tag_syntax_is_refused(string service)
    {
        Assert.Null(Parse($$"""{"service":{{JsonSerializer.Serialize(service)}},"lookback":"1h"}""", SourceKinds.Tempo,
            out var error));
        Assert.Equal(
            "The service name must be 1 to 256 characters, with no space, control character, '\"', '=' or '\\'.",
            error);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("checkout-api.v2/eu_west:1")]
    [InlineData("caf\u00e9")]
    public void An_ordinary_service_name_is_accepted(string service)
    {
        Assert.NotNull(Parse($$"""{"service":{{JsonSerializer.Serialize(service)}},"lookback":"1h"}""",
            SourceKinds.Tempo,
            out _));
    }

    [Fact]
    public void An_absolute_window_must_be_ordered_and_already_past()
    {
        Assert.Null(Parse($$"""{"service":"orders","from_ms":{{Now - 1000}},"to_ms":{{Now - 2000}}}""",
            SourceKinds.Tempo, out var reversed));
        Assert.Equal("The window's start must come before its end.", reversed);

        Assert.Null(Parse($$"""{"service":"orders","from_ms":{{Now}},"to_ms":{{Now + 3_600_000}}}""",
            SourceKinds.Tempo, out var future));
        Assert.Equal("The window's end cannot be in the future.", future);
    }

    [Fact]
    public void Max_traces_is_bounded_by_the_service_cap()
    {
        Assert.Null(Parse("""{"service":"orders","lookback":"1h","max_traces":2001}""",
            SourceKinds.Tempo, out var above));
        Assert.Equal("max_traces must be between 1 and 2000.", above);
        Assert.NotNull(Parse("""{"service":"orders","lookback":"1h","max_traces":2000}""",
            SourceKinds.Tempo, out _));
    }

    [Fact]
    public void A_non_numeric_timestamp_is_absent_rather_than_a_crash()
    {
        // JsonElement.TryGetInt64 throws on a string element instead of
        // returning false, so a wrong type must not reach it.
        Assert.Null(Parse("""{"service":"orders","from_ms":"yesterday","to_ms":"today"}""",
            SourceKinds.Tempo, out var error));
        Assert.Equal("A service request needs either a lookback or an absolute window.", error);
    }

    [Fact]
    public void A_relative_window_becomes_the_engine_command_line()
    {
        var request = Parse("""{"service":"order-service","lookback":"90m","max_traces":250}""",
            SourceKinds.Tempo, out _);

        Assert.Equal(
            [
                "tempo", "--endpoint", "http://tempo.example:3200", "--format", "json",
                "--service", "order-service", "--lookback", "90m", "--max-traces", "250"
            ],
            request!.ToEngineArguments(Source(SourceKinds.Tempo), null));
    }

    [Fact]
    public void An_absolute_window_becomes_iso_8601_utc()
    {
        var request = Parse("""{"service":"orders","from_ms":1787835540600,"to_ms":1787838540400}""",
            SourceKinds.JaegerQuery, out _);

        var arguments = request!.ToEngineArguments(Source(SourceKinds.JaegerQuery), null).ToList();

        Assert.Equal("jaeger-query", arguments[0]);
        // The residual milliseconds are truncated: the engine takes whole seconds.
        Assert.Equal("2026-08-27T12:59:00Z", arguments[arguments.IndexOf("--from") + 1]);
        Assert.Equal("2026-08-27T13:49:00Z", arguments[arguments.IndexOf("--to") + 1]);
        // The engine refuses --lookback alongside --from/--to, so an absolute
        // window must not carry one.
        Assert.DoesNotContain("--lookback", arguments);
        // Absent from the submission, so the Hub sends the engine's own default
        // rather than leaving the cap unstated in the stored run.
        Assert.Equal("100", arguments[arguments.IndexOf("--max-traces") + 1]);
    }

    [Fact]
    public void A_trace_request_carries_no_window_at_all()
    {
        var request = Parse("""{"trace_id":"abc123def456"}""", SourceKinds.Tempo, out _);

        var arguments = request!.ToEngineArguments(Source(SourceKinds.Tempo), null);

        Assert.Equal([
            "tempo", "--endpoint", "http://tempo.example:3200", "--format", "json",
            "--trace-id", "abc123def456"
        ], arguments);
    }

    [Theory]
    [InlineData(SourceKinds.Tempo, "tempo")]
    [InlineData(SourceKinds.JaegerQuery, "jaeger-query")]
    public void The_endpoint_and_subcommand_a_command_would_publish_are_the_ones_the_engine_receives(
        string kind, string subcommand)
    {
        // The launcher prints a command out of EngineSubcommand and
        // PublicEndpointArgument, which spells the same URL the same way when
        // no public URL is declared. Inlining either back into
        // ToEngineArguments would let the printed command and the launched run
        // target different things, silently.
        var source = Source(kind) with { BaseUrl = new Uri("http://backend.example:3200/prefix/") };
        var request = Parse("""{"trace_id":"abc123def456"}""", kind, out _);

        var arguments = request!.ToEngineArguments(source, null).ToList();

        Assert.Equal(subcommand, arguments[0]);
        Assert.Equal(source.EngineSubcommand, arguments[0]);
        Assert.Equal(source.EndpointArgument, arguments[2]);
        Assert.Equal(source.PublicEndpointArgument, arguments[2]);
        // The trailing slash is dropped once, in the one place both readers use.
        Assert.Equal("http://backend.example:3200/prefix", arguments[2]);
        // A public URL only changes what is printed: the Hub still runs from
        // inside the cluster, against the URL it polls.
        var published = source with { PublicUrl = new Uri("https://backend.example/") };
        Assert.Equal("http://backend.example:3200/prefix", request.ToEngineArguments(published, null)[2]);
    }

    [Fact]
    public void An_authenticated_source_reads_its_header_from_the_environment()
    {
        // The flag names the same variable the launcher's printed command
        // does, and the value itself never enters the argument list.
        var source = Source(SourceKinds.Tempo) with
        {
            AuthHeaderName = "Authorization",
            AuthHeaderValue = "Bearer secret" // gitleaks:allow -- synthetic test credential
        };
        var service = Parse("""{"service":"orders","lookback":"1h"}""", SourceKinds.Tempo, out _);
        var trace = Parse("""{"trace_id":"abc123def456"}""", SourceKinds.Tempo, out _);

        foreach (var arguments in new[]
                 {
                     service!.ToEngineArguments(source, null),
                     trace!.ToEngineArguments(source, null)
                 })
        {
            var flag = arguments.ToList().IndexOf("--auth-header-env");
            Assert.True(flag >= 0);
            Assert.Equal(AnalysisRequest.AuthTokenVariable, arguments[flag + 1]);
            Assert.DoesNotContain(arguments, argument => argument.Contains("secret"));
        }

        Assert.DoesNotContain(
            "--auth-header-env",
            service.ToEngineArguments(Source(SourceKinds.Tempo), null));
        // A header only the public route asks for is the operator's business:
        // the Hub runs over the cluster network and holds no value for it.
        Assert.DoesNotContain(
            "--auth-header-env",
            service.ToEngineArguments(
                Source(SourceKinds.Tempo) with
                {
                    PublicUrl = new Uri("https://backend.example"),
                    PublicAuthHeaderName = "Authorization"
                },
                null));
    }

    [Fact]
    public void A_daemon_has_no_subcommand_because_it_is_read_rather_than_queried()
    {
        Assert.Null(Source(SourceKinds.Daemon).EngineSubcommand);
    }

    private static AnalysisRequest? Parse(string payload, string kind, out string? error,
        AnalysisOptions? analysis = null)
    {
        using var document = JsonDocument.Parse(payload);
        return AnalysisRequest.TryParse(
            document.RootElement,
            Source(kind),
            analysis ?? new AnalysisOptions(),
            Now,
            out error);
    }

    [Fact]
    public void A_detection_override_becomes_a_config_the_engine_is_pointed_at()
    {
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"n_plus_one_min_occurrences":13}}
                            """, SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal("[detection]\nn_plus_one_min_occurrences = 13\n", request!.Detection.ToToml());
        var arguments = request.ToEngineArguments(Source(SourceKinds.Tempo), "/data/reports/run.config.toml").ToList();
        Assert.Equal("/data/reports/run.config.toml", arguments[arguments.IndexOf("-c") + 1]);
    }

    [Fact]
    public void A_value_equal_to_the_engine_default_is_not_recorded_as_an_override()
    {
        // Writing it out would make the run card claim a departure from the
        // standard configuration where there is none.
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"n_plus_one_min_occurrences":5}}
                            """, SourceKinds.Tempo, out _);

        Assert.True(request!.Detection.IsEmpty);
    }

    [Fact]
    public void The_classification_mode_is_written_as_a_quoted_string()
    {
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"sanitizer_aware_classification":"strict"}}
                            """, SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal("[detection]\nsanitizer_aware_classification = \"strict\"\n", request!.Detection.ToToml());
    }

    [Theory]
    // TOML tells an integer from a float by the point. The engine reads `1` into
    // its float all the same, but `1.0` says what was meant.
    [InlineData("1", "1.0")]
    [InlineData("0.75", "0.75")]
    public void The_variance_threshold_keeps_a_decimal_point(string sent, string written)
    {
        var request = Parse(
            $$$"""{"service":"orders","lookback":"1h","detection":{"sanitizer_aware_min_cv":{{{sent}}}}}""",
            SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal($"[detection]\nsanitizer_aware_min_cv = {written}\n", request!.Detection.ToToml());
    }

    [Theory]
    [InlineData("""{"sanitizer_aware_classification":"auto"}""")]
    [InlineData("""{"sanitizer_aware_min_cv":0.5}""")]
    public void The_sanitizer_defaults_are_not_recorded_as_overrides(string detection)
    {
        var request = Parse(
            $$"""{"service":"orders","lookback":"1h","detection":{{detection}}}""",
            SourceKinds.Tempo, out _);

        Assert.True(request!.Detection.IsEmpty);
    }

    [Theory]
    // The mode dates back to engine 0.5.7, the variance threshold to 0.18.0.
    [InlineData("0.17.0", true, false)]
    [InlineData("0.18.0", true, true)]
    [InlineData("0.19.2", true, true)]
    // A pre-release of the minor that added a knob reads it too, and so does a
    // build-metadata suffix, which is the other half of what the parser strips.
    [InlineData("0.18.0-rc.1", true, true)]
    [InlineData("0.18.0+build.5", true, true)]
    [InlineData("0.5.7", true, false)]
    // No probed version withholds only what a version gates: the mode is read by
    // every engine, so a failed probe still offers it.
    [InlineData(null, true, false)]
    public void The_sanitizer_knobs_are_offered_only_to_an_engine_that_reads_them(
        string? engine, bool mode, bool threshold)
    {
        var names = DetectionOverrides.SchemaFor(engine).Select(knob => knob.Name).ToList();

        Assert.Equal(mode, names.Contains("sanitizer_aware_classification"));
        Assert.Equal(threshold, names.Contains("sanitizer_aware_min_cv"));
        // The eight thresholds predate the gate and are always there.
        Assert.Contains("n_plus_one_min_occurrences", names);
    }

    [Theory]
    [InlineData("0.42.1", false)]
    [InlineData("0.43.0", true)]
    [InlineData("0.43.0-rc.1", true)]
    [InlineData("0.44.2", true)]
    [InlineData(null, false)]
    [InlineData("illisible", false)]
    public void Fetch_concurrency_is_read_from_engine_0_43_0(string? engine, bool reads)
    {
        Assert.Equal(reads, DetectionOverrides.ReadsFetchConcurrency(engine));
    }

    [Fact]
    public void A_knob_the_engine_cannot_read_is_refused_at_submission_not_at_run_time()
    {
        var request = Parse("""
                            {"service":"orders","lookback":"1h","detection":{"sanitizer_aware_min_cv":0.75}}
                            """, SourceKinds.Tempo, out var error);
        Assert.Null(error);

        Assert.Equal(
            "sanitizer_aware_min_cv needs engine 0.18.0 or later, this Hub runs 0.17.0.",
            request!.Detection.RefusedBy("0.17.0"));
        Assert.Null(request.Detection.RefusedBy("0.18.0"));
        // A version the probe never read, or read but could not parse, is named
        // for what it is rather than called too old.
        Assert.Equal(
            "sanitizer_aware_min_cv needs engine 0.18.0 or later, this Hub has not read its engine's version.",
            request.Detection.RefusedBy(null));
        Assert.Equal(
            "sanitizer_aware_min_cv needs engine 0.18.0 or later, this Hub could not read its engine's version (0.18.0.rc1).",
            request.Detection.RefusedBy("0.18.0.rc1"));
        // The mode is older than any engine the Hub drives, so it is never the
        // reason, not even when the probe failed.
        var mode = Parse("""
                         {"service":"orders","lookback":"1h","detection":{"sanitizer_aware_classification":"strict"}}
                         """, SourceKinds.Tempo, out _);
        Assert.Null(mode!.Detection.RefusedBy("0.17.0"));
        Assert.Null(mode.Detection.RefusedBy(null));
    }

    [Theory]
    // Bounds mirror the engine's validator: two of them floor at 2, not 1.
    [InlineData("""{"n_plus_one_min_occurrences":0}""")]
    [InlineData("""{"pool_saturation_concurrent_threshold":1}""")]
    [InlineData("""{"serialized_min_sequential":1}""")]
    [InlineData("""{"max_fanout":100001}""")]
    [InlineData("""{"n_plus_one_min_occurrences":"many"}""")]
    [InlineData("""{"no_such_setting":3}""")]
    [InlineData("""{"sanitizer_aware_classification":"loose"}""")]
    [InlineData("""{"sanitizer_aware_classification":3}""")]
    [InlineData("""{"sanitizer_aware_min_cv":0}""")]
    [InlineData("""{"sanitizer_aware_min_cv":10.5}""")]
    [InlineData("""{"sanitizer_aware_min_cv":"high"}""")]
    public void An_out_of_range_or_unknown_detection_setting_is_refused(string detection)
    {
        Assert.Null(Parse(
            $$"""{"service":"orders","lookback":"1h","detection":{{detection}}}""",
            SourceKinds.Tempo, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_daemon_takes_no_detection_override()
    {
        // It detects with its own configuration, so the Hub cannot honour one.
        Assert.Null(Parse("""{"detection":{"max_fanout":50}}""", SourceKinds.Daemon, out var error));
        Assert.Equal(
            "A daemon snapshot takes no parameters, and detects with its own configuration.",
            error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(64)]
    public void A_tempo_service_request_takes_a_fetch_concurrency_and_keeps_it(int value)
    {
        var stored = $$"""{"service":"orders","lookback":"1h","fetch_concurrency":{{value}}}""";

        var request = Parse(stored, SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal(stored, request!.ToJson());
        Assert.Equal(stored, Parse(stored, SourceKinds.Tempo, out _)!.ToJson());
    }

    [Fact]
    public void A_fetch_concurrency_is_bounded_by_the_configured_max()
    {
        var options = new AnalysisOptions { FetchConcurrencyWarn = 8, FetchConcurrencyMax = 8 };

        Assert.NotNull(Parse("""{"service":"orders","lookback":"1h","fetch_concurrency":8}""",
            SourceKinds.Tempo, out _, options));
        Assert.Null(Parse("""{"service":"orders","lookback":"1h","fetch_concurrency":9}""",
            SourceKinds.Tempo, out var error, options));
        Assert.Equal("fetch_concurrency must be a whole number between 1 and 8.", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("65")]
    // A decimal and a string are present and wrong, not absent: nothing is dropped silently.
    [InlineData("4.5")]
    [InlineData("4.0")]
    [InlineData("\"8\"")]
    [InlineData("true")]
    [InlineData("12345678901")]
    public void A_fetch_concurrency_outside_the_range_or_not_a_whole_number_is_refused(string value)
    {
        Assert.Null(Parse($$"""{"service":"orders","lookback":"1h","fetch_concurrency":{{value}}}""",
            SourceKinds.Tempo, out var error));
        Assert.Equal("fetch_concurrency must be a whole number between 1 and 64.", error);
    }

    [Fact]
    public void A_null_fetch_concurrency_is_an_absent_one()
    {
        var request = Parse("""{"service":"orders","lookback":"1h","fetch_concurrency":null}""",
            SourceKinds.Tempo, out var error);

        Assert.Null(error);
        Assert.Equal("""{"service":"orders","lookback":"1h"}""", request!.ToJson());
    }

    [Theory]
    [InlineData("""{"service":"orders","lookback":"1h","fetch_concurrency":4}""", SourceKinds.JaegerQuery)]
    [InlineData("""{"fetch_concurrency":4}""", SourceKinds.Daemon)]
    [InlineData("""{"trace_id":"abc123","fetch_concurrency":4}""", SourceKinds.Tempo)]
    [InlineData("""{"trace_id":"abc123","fetch_concurrency":4}""", SourceKinds.JaegerQuery)]
    public void A_fetch_concurrency_is_refused_where_the_engine_has_no_such_setting(string payload, string kind)
    {
        Assert.Null(Parse(payload, kind, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_fetch_concurrency_follows_max_traces_on_the_command_line()
    {
        var request = Parse("""{"service":"orders","lookback":"90m","max_traces":250,"fetch_concurrency":4}""",
            SourceKinds.Tempo, out _);

        Assert.Equal(
            [
                "tempo", "--endpoint", "http://tempo.example:3200", "--format", "json",
                "--service", "orders", "--lookback", "90m", "--max-traces", "250", "--fetch-concurrency", "4"
            ],
            request!.ToEngineArguments(Source(SourceKinds.Tempo), null));
    }

    [Theory]
    [InlineData("0.43.0", 64, 16)]
    [InlineData("0.43.0", 8, 8)]
    [InlineData("0.44.1", 16, 16)]
    public void A_request_without_a_value_is_given_the_engine_default_held_under_the_max(
        string engine, int max, int expected)
    {
        var options = new AnalysisOptions { FetchConcurrencyWarn = Math.Min(16, max), FetchConcurrencyMax = max };
        var request = Parse("""{"service":"orders","lookback":"1h"}""", SourceKinds.Tempo, out _, options)!;

        var written = request.WithDefaultFetchConcurrency(Source(SourceKinds.Tempo), options, engine);

        Assert.Null(request.RefusedBy(Source(SourceKinds.Tempo), options, engine));
        Assert.Equal($$"""{"service":"orders","lookback":"1h","fetch_concurrency":{{expected}}}""",
            written.ToJson());
    }

    [Theory]
    // An explicit value is kept, and no engine older than 0.43.0 gets one it would refuse.
    [InlineData("""{"service":"orders","lookback":"1h","fetch_concurrency":4}""", "0.43.0", SourceKinds.Tempo)]
    [InlineData("""{"service":"orders","lookback":"1h"}""", "0.42.1", SourceKinds.Tempo)]
    [InlineData("""{"service":"orders","lookback":"1h"}""", null, SourceKinds.Tempo)]
    // A source and a mode the engine has no setting for.
    [InlineData("""{"service":"orders","lookback":"1h"}""", "0.43.0", SourceKinds.JaegerQuery)]
    [InlineData("""{"trace_id":"abc123"}""", "0.43.0", SourceKinds.Tempo)]
    [InlineData("""{}""", "0.43.0", SourceKinds.Daemon)]
    public void A_default_is_written_only_where_a_tempo_service_run_would_read_it(
        string payload, string? engine, string kind)
    {
        var options = new AnalysisOptions { FetchConcurrencyWarn = 8, FetchConcurrencyMax = 8 };
        var request = Parse(payload, kind, out _, new AnalysisOptions())!;

        var written = request.WithDefaultFetchConcurrency(Source(kind), options, engine);

        Assert.Equal(request.ToJson(), written.ToJson());
    }

    [Fact]
    public void An_engine_that_cannot_read_fetch_concurrency_refuses_a_value_naming_both_versions()
    {
        var request = Parse("""{"service":"orders","lookback":"1h","fetch_concurrency":4}""",
            SourceKinds.Tempo, out _)!;
        var source = Source(SourceKinds.Tempo);
        var options = new AnalysisOptions();

        Assert.Equal("fetch_concurrency needs engine 0.43.0 or later, this Hub runs 0.42.1.",
            request.RefusedBy(source, options, "0.42.1"));
        Assert.Equal("fetch_concurrency needs engine 0.43.0 or later, this Hub has not read its engine's version.",
            request.RefusedBy(source, options, null));
        Assert.Null(request.RefusedBy(source, options, "0.43.0"));
        // Not a Tempo run: no flag to refuse.
        Assert.Null(Parse("""{"service":"orders","lookback":"1h"}""", SourceKinds.JaegerQuery, out _)!
            .RefusedBy(Source(SourceKinds.JaegerQuery), options, "0.42.1"));
    }

    [Theory]
    [InlineData("0.42.1", "this Hub runs 0.42.1")]
    [InlineData(null, "this Hub has not read its engine's version")]
    [InlineData("0.43.0.rc1", "this Hub could not read its engine's version (0.43.0.rc1)")]
    public void A_max_under_the_old_default_is_refused_on_an_engine_that_cannot_be_told_to_stay_under_it(
        string? engine, string named)
    {
        var options = new AnalysisOptions { FetchConcurrencyWarn = 8, FetchConcurrencyMax = 8 };
        var request = Parse("""{"service":"orders","lookback":"1h"}""", SourceKinds.Tempo, out _, options)!;

        var refused = request.RefusedBy(Source(SourceKinds.Tempo), options, engine);

        Assert.NotNull(refused);
        Assert.Contains("8", refused, StringComparison.Ordinal);
        Assert.Contains(named, refused, StringComparison.Ordinal);
        // At 16 and above the old engine's own default already fits.
        Assert.Null(request.RefusedBy(Source(SourceKinds.Tempo),
            new AnalysisOptions { FetchConcurrencyMax = 16 }, engine));
    }

    private static SourceOptions Source(string kind)
    {
        return new SourceOptions
        {
            Id = "target",
            Name = "Target",
            Environment = "production",
            Kind = kind,
            BaseUrl = new Uri("http://tempo.example:3200")
        };
    }
}
