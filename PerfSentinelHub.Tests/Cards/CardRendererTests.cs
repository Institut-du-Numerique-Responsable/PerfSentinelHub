using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Recaps;
using PerfSentinelHub.Storage;
using static PerfSentinelHub.Tests.Support.CardSeed;

namespace PerfSentinelHub.Tests.Cards;

/// <summary>
///     The engine's card render and verify-card contract as the Hub reads it: card.json and the key as
///     files in a private directory gone afterwards, the result line, never stderr.
/// </summary>
public sealed partial class CardRendererTests : IDisposable
{
    private const string Fingerprint = "ps1:AAAAAAAAAAAAAAAAAAAAAA";
    private const string N1 = "n_plus_one_sql:order-svc:0000000000000000000000000000000000000001";
    private const string KeyBytes = "fake pkcs8 private key";

    private static readonly StoredCardKey Key = new(Fingerprint, new string('A', 43), 0, null, null);

    // Engine plan 2's card.json, key by key; a figures key outside the angle's set fails every real render.
    private static readonly string[] Top =
    [
        "angle", "lang", "period", "scope", "figures", "free", "org", "generated_at_ms", "period.kind", "period.from",
        "period.to", "scope.environment", "scope.sources", "scope.covered_days", "scope.restart_days",
        "scope.sampled_permille", "org.name"
    ];

    private static readonly string[] StoryKeys =
        ["story.finding_type", "story.service", "story.endpoint", "story.observed_at", "story.done_at"];

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-cardr-{Guid.NewGuid():N}");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Reports => Path.Combine(_workspace, "reports");

    public void Dispose()
    {
        TestPool.ClearFor(Path.Combine(_workspace, "hub.db"));
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    [Fact]
    public async Task The_card_json_carries_the_hub_figures_and_free_fields()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true });

        var outcome = await renderer.RenderAsync(Collective(), Key, KeyFile(), "png", Token);

        var rendered = Assert.IsType<CardRender.Rendered>(outcome);
        byte[] png = [0x89, .. "PNG fake"u8];
        Assert.Equal(png, rendered.Bytes);
        Assert.Equal("https://perf-sentinel.dev/verifier#c=e30.AA", rendered.CompactUrl);
        var json = await File.ReadAllTextAsync(Path.Combine(Reports, "card-input.json"), Token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("collective", root.GetProperty("angle").GetString());
        Assert.Equal("fr", root.GetProperty("lang").GetString());
        Assert.Equal("month", root.GetProperty("period").GetProperty("kind").GetString());
        Assert.Equal("Acme", root.GetProperty("org").GetProperty("name").GetString());
        Assert.Equal("Our month", root.GetProperty("free").GetProperty("title").GetString());
        Assert.False(root.TryGetProperty("equivalence", out _));
        Assert.False(root.TryGetProperty("key", out _));
        Assert.DoesNotContain(KeyBytes, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_card_json_has_exactly_the_engine_keys()
    {
        var service = await SeededServiceAsync();
        var renderer = await RendererAsync(new FakeEngine { Card = true });
        string[] storyFree = ["free.title", "free.team", "free.label"];
        string[] collectiveFigures =
        [
            "figures.confirmed_fixes_count", "figures.previous_fixes_count", "figures.services_count",
            "figures.fixes_by_family_count", "figures.fixes_by_family_count.extra_calls",
            "figures.fixes_by_family_count.slow_calls", "figures.fixes_by_family_count.call_architecture",
            "figures.before_fix_ops", "figures.co2_low_ug"
        ];
        string[] beforeFix = ["before_fix_period", "before_fix_period.from", "before_fix_period.to"];

        var story = await WrittenKeysAsync(service, renderer, "story", new CardFree("Fixed", "Checkout", "Orders"));
        var curve = await WrittenKeysAsync(service, renderer, "curve", new CardFree(null, null, null));
        var collective = await WrittenKeysAsync(service, renderer, "collective", new CardFree(null, null, null));

        Assert.Equal(Keys([.. Top, "story", .. beforeFix, .. storyFree], StoryKeys,
                ["figures.cited_ops", "figures.before_fix_ops", "figures.co2_low_ug"]), story);
        Assert.Equal(Keys(Top,
            ["figures.waste_permille", "figures.avoidable_ops", "figures.co2_low_ug"]), curve);
        Assert.Equal(Keys([.. Top, .. beforeFix], collectiveFigures), collective);
    }

    [Fact]
    public async Task The_signing_key_travels_as_a_file_in_the_private_directory()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true });

        Assert.IsType<CardRender.Rendered>(await renderer.RenderAsync(Collective(), Key, KeyFile(), "pdf", Token));

        var arguments = (await File.ReadAllTextAsync(Path.Combine(Reports, "card-args.txt"), Token)).Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var input = arguments[Array.IndexOf(arguments, "--input") + 1];
        var directory = Path.GetDirectoryName(input)!;
        Assert.Equal(["card", "render"], arguments[..2]);
        Assert.Equal(Path.Combine(directory, "card.json"), input);
        Assert.Equal(Path.Combine(directory, "key.pk8"), arguments[Array.IndexOf(arguments, "--signing-key") + 1]);
        Assert.Equal(Path.Combine(directory, "out"), arguments[Array.IndexOf(arguments, "--out-dir") + 1]);
        Assert.Equal("pdf", arguments[Array.IndexOf(arguments, "--format") + 1]);
        Assert.Equal(KeyBytes, await File.ReadAllTextAsync(Path.Combine(Reports, "card-key-seen.pk8"), Token));
        Assert.False(Directory.Exists(directory), "the private directory must be deleted");
    }

    [Fact]
    public async Task An_svg_render_passes_the_format_and_reads_card_svg()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true });

        var rendered = Assert.IsType<CardRender.Rendered>(
            await renderer.RenderAsync(Collective(), Key, KeyFile(), "svg", Token));

        Assert.Contains("--format svg", await File.ReadAllTextAsync(Path.Combine(Reports, "card-args.txt"), Token),
            StringComparison.Ordinal);
        Assert.Equal(FakeEngine.CardSvg, Encoding.UTF8.GetString(rendered.Bytes));
    }

    [Fact]
    public async Task A_jxl_render_passes_the_format_and_reads_card_jxl()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true });

        var rendered = Assert.IsType<CardRender.Rendered>(
            await renderer.RenderAsync(Collective(), Key, KeyFile(), "jxl", Token));

        Assert.Contains("--format jxl", await File.ReadAllTextAsync(Path.Combine(Reports, "card-args.txt"), Token),
            StringComparison.Ordinal);
        Assert.Equal(FakeEngine.CardJxl, rendered.Bytes);
    }

    [Fact]
    public async Task A_fingerprint_other_than_the_key_is_out_of_contract()
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            Card = true,
            CardRenderOutput =
                """{"result":"rendered","files":["card.png"],"fingerprint":"ps1:BBBBBBBBBBBBBBBBBBBBBB","compact_url":"https://perf-sentinel.dev/verifier#c=e30.AA"}"""
        });

        Assert.IsType<CardRender.Malformed>(await renderer.RenderAsync(Collective(), Key, KeyFile(), "png", Token));
    }

    [Theory]
    [InlineData(5, """{"result":"unrenderable","reason":"unsupported_characters","field":"free.team"}""",
        "unsupported_characters", "free.team")]
    [InlineData(5, """{"result":"unrenderable","reason":"compact_too_large"}""", "compact_too_large", null)]
    [InlineData(1, """{"result":"refused","reason":"volume_not_avoidable"}""", "volume_not_avoidable", null)]
    public async Task A_reason_line_is_a_refusal(int exitCode, string line, string reason, string? field)
    {
        var renderer = await RendererAsync(new FakeEngine
        { Card = true, CardRenderExitCode = exitCode, CardRenderOutput = line });

        var outcome = await renderer.RenderAsync(Collective(), Key, KeyFile(), "png", Token);

        Assert.Equal(new CardRender.Refused(reason, field), outcome);
    }

    [Theory]
    [InlineData(3, "")]
    [InlineData(5, "not json")]
    [InlineData(0, "{}")]
    public async Task A_line_without_its_keys_is_out_of_contract(int exitCode, string line)
    {
        var renderer = await RendererAsync(new FakeEngine
        { Card = true, CardRenderExitCode = exitCode, CardRenderOutput = line });

        Assert.IsType<CardRender.Malformed>(await renderer.RenderAsync(Collective(), Key, KeyFile(), "png", Token));
    }

    [Fact]
    public async Task A_key_file_gone_before_the_copy_is_out_of_contract()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true });

        var outcome = await renderer.RenderAsync(Collective(), Key, Path.Combine(_workspace, "gone.pk8"), "png",
            Token);

        Assert.IsType<CardRender.Malformed>(outcome);
        Assert.False(File.Exists(Path.Combine(Reports, "card-args.txt")));
    }

    [Fact]
    public async Task A_timeout_is_reported()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true, SleepSeconds = 5 });
        renderer.Timeout = TimeSpan.FromMilliseconds(300);

        Assert.IsType<CardRender.Timeout>(await renderer.RenderAsync(Collective(), Key, KeyFile(), "png", Token));
    }

    [Fact]
    public async Task An_engine_without_card_is_unsupported_and_never_run()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = false });

        Assert.IsType<CardRender.Unsupported>(await renderer.RenderAsync(Collective(), Key, KeyFile(), "png", Token));
        Assert.Null(await renderer.VerifyAsync([1, 2, 3], Token));
        Assert.False(File.Exists(Path.Combine(Reports, "card-args.txt")));
    }

    [Fact]
    public async Task Verify_reads_the_verdict_and_names_the_doh_service()
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            Card = true,
            VerifyCardExitCode = 4,
            VerifyCardOutput =
                """{"integrity":"ok","content":"intact","level":1,"domain":"example.com","key":"unknown","reason":"binding_check_failed"}"""
        });

        var verdict = await renderer.VerifyAsync([0x89, .. "PNG fake"u8], Token);

        Assert.NotNull(verdict);
        Assert.Equal(("ok", 1, "example.com", "unknown", "binding_check_failed"),
            (verdict.Integrity, verdict.Level, verdict.Domain, verdict.Key, verdict.Reason));
        var arguments = (await File.ReadAllTextAsync(Path.Combine(Reports, "verify-args.txt"), Token)).Trim();
        Assert.Matches(@"\Averify-card \S+card\.png --online --doh https://cloudflare-dns\.com/dns-query\z", arguments);
        Assert.False(Directory.Exists(Path.GetDirectoryName(arguments.Split(' ')[1])));
    }

    [Theory]
    [InlineData("""{"result":"input_error","reason":"no_domain","content":"intact"}""")]
    [InlineData("""{"integrity":"ok","content":"intact","level":"2","domain":null,"key":"current","reason":null}""")]
    [InlineData("""{"integrity":"ok","content":"intact","level":2,"key":"current","reason":null}""")]
    [InlineData("""{"integrity":"ok","content":"intact","level":2,"domain":null,"reason":null}""")]
    [InlineData("not json")]
    public async Task A_verdict_with_a_missing_or_mistyped_key_is_null(string line)
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true, VerifyCardExitCode = 3, VerifyCardOutput = line });

        Assert.Null(await renderer.VerifyAsync([1], Token));
    }

    [Fact]
    public async Task Verify_reads_the_content_verdict_and_the_figures()
    {
        var renderer = await RendererAsync(new FakeEngine { Card = true });

        var verdict = await renderer.VerifyAsync([1], Token);

        Assert.NotNull(verdict);
        Assert.Equal("intact", verdict.Content);
        Assert.NotNull(verdict.Figures);
        Assert.Equal(("story", 41L, "2026-09-01", "2026-07-01"),
            (verdict.Figures.Angle, verdict.Figures.CitedOps, verdict.Figures.Period.From,
                verdict.Figures.BeforeFixPeriod?.From));
        Assert.Equal(1937000L, verdict.Figures.BeforeFixOps);
        Assert.Null(verdict.Figures.CitedMs);
    }

    [Fact]
    public async Task A_verify_line_without_content_is_out_of_contract()
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            Card = true,
            VerifyCardOutput = """{"integrity":"ok","level":2,"domain":"example.com","key":"current","reason":null}"""
        });

        Assert.Null(await renderer.VerifyAsync([1], Token));
    }

    [Fact]
    public async Task A_verify_line_with_an_unknown_content_is_out_of_contract()
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            Card = true,
            VerifyCardOutput =
                """{"integrity":"ok","content":"maybe","level":2,"domain":"example.com","key":"current","reason":null}"""
        });

        Assert.Null(await renderer.VerifyAsync([1], Token));
    }

    [Theory]
    [InlineData("""{"period":{"kind":"month","from":"2026-09-01","to":"2026-09-30"},"cited_ops":41}""")]
    [InlineData("""{"angle":"story","period":{"kind":"month","from":"2026-09-01"},"cited_ops":41}""")]
    [InlineData("""{"angle":"story","cited_ops":41}""")]
    [InlineData("""{"angle":"story","period":{"kind":"month","from":"2026-09-01","to":"2026-09-30"},"cited_ops":"41"}""")]
    [InlineData("""{"angle":"story","period":{"kind":"month","from":"2026-09-01","to":"2026-09-30"},"cited_ops":18446744073709551615}""")]
    [InlineData("""[41]""")]
    public async Task A_verify_line_with_unreadable_figures_is_out_of_contract(string figures)
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            Card = true,
            VerifyCardOutput =
                $$"""{"integrity":"ok","content":"intact","level":2,"domain":"example.com","key":"current","reason":null,"figures":{{figures}}}"""
        });

        Assert.Null(await renderer.VerifyAsync([1], Token));
    }

    [Fact]
    public async Task A_verify_line_without_figures_reads_none()
    {
        var renderer = await RendererAsync(new FakeEngine
        {
            Card = true,
            VerifyCardExitCode = 1,
            VerifyCardOutput =
                """{"integrity":"ok","content":"modified","level":1,"domain":null,"key":"unknown","reason":"content_modified","figures":null}"""
        });

        var verdict = await renderer.VerifyAsync([1], Token);

        Assert.NotNull(verdict);
        Assert.Equal(("modified", "content_modified"), (verdict.Content, verdict.Reason));
        Assert.Null(verdict.Figures);
    }

    // ------------------------------------------------------------ helpers

    private static CardInput Collective()
    {
        return new CardInput(
            "collective", "fr", new CardPeriod("month", "2026-10-01", "2026-10-31"),
            new CardScope("prod", ["prod-eu"], 31, 0, 1000),
            new CardFiguresBlock(ConfirmedFixesCount: 1, ServicesCount: 1, FixesByFamilyCount: new CardFamilies(1, 0, 0)),
            null, null, new CardFree("Our month", null, null), new CardOrg("Acme", null), NowMs);
    }

    private static string[] Keys(params string[][] groups)
    {
        return [.. groups.SelectMany(group => group).Distinct().Order(StringComparer.Ordinal)];
    }

    private string KeyFile()
    {
        var path = Path.Combine(_workspace, "key.pk8");
        File.WriteAllText(path, KeyBytes);
        return path;
    }

    // Renders the angle CardService computes and answers the written card.json's key paths, checking
    // on the way that every number is an integer and every date a yyyy-MM-dd string.
    private async Task<string[]> WrittenKeysAsync(CardService service, CardRenderer renderer, string angle,
        CardFree free)
    {
        var ask = new CardAsk(new CardScopeAsk(RecapPeriodKind.Month, October.From, ["a"], ["a"], "prod", null,
            Sources), angle, "en", null, free);
        var input = Assert.IsType<CardOutcome.Ready>(await service.ComputeAsync(ask, Token)).Input;
        Assert.IsType<CardRender.Rendered>(await renderer.RenderAsync(input, Key, KeyFile(), "png", Token));
        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(Reports, "card-input.json"), Token));
        var paths = new List<string>();
        Walk(document.RootElement, "", paths);
        return [.. paths.Order(StringComparer.Ordinal)];
    }

    private static void Walk(JsonElement element, string path, List<string> paths)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    paths.Add(child);
                    Walk(property.Value, child, paths);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, path, paths);
                break;
            case JsonValueKind.Number:
                Assert.True(element.TryGetInt64(out _), $"{path} must be an integer");
                break;
            case JsonValueKind.String when path is "period.from" or "period.to" or "story.observed_at"
                or "story.done_at" or "before_fix_period.from" or "before_fix_period.to":
                Assert.Matches(Day(), element.GetString()!);
                Assert.True(DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _));
                break;
        }
    }

    // October: one n+1 fix with a volume in September, totals and carbon both months; schema V20 applied
    // in August, so the collective counts September's fixes.
    private async Task<CardService> SeededServiceAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(Now, CultureInfo.InvariantCulture));
        var options = Options.Create(new HubOptions
        {
            DatabasePath = Path.Combine(_workspace, "hub.db"),
            Sources = [.. Sources],
            Cards = new CardsOptions { Organisation = "Acme" }
        });
        Directory.CreateDirectory(_workspace);
        var database = new HubDatabase(options, clock);
        await database.InitializeAsync(Token);
        var seed = new CardSeed(database);
        await seed.LedgerAsync("a",
            [
                .. Sealed(September,
                    day => day with { AvoidableGco2Low = 3.0, AvoidableIoOps = 1000, TotalIoOps = 10_000 }),
                .. Sealed(October, day => day with { AvoidableGco2Low = 0.5, AvoidableIoOps = 100, TotalIoOps = 10_000 })
            ],
            new DetectionCountRow(September.FromDay, N1, 2, 100));
        await seed.FixAsync("a", N1, October.FromDay + 14);
        return new CardService(database, options, clock);
    }

    private async Task<CardRenderer> RendererAsync(FakeEngine engine)
    {
        var options = Options.Create(new HubOptions
        {
            Analysis = new AnalysisOptions
            {
                EngineBinaryPath =
                    (engine with { Version = "perf-sentinel 0.39.0" }).WriteTo(Path.Combine(_workspace, "perf-sentinel")),
                ReportDirectory = Reports
            }
        });
        var probe = new EngineProbe(options, NullLogger<EngineProbe>.Instance);
        await probe.StartAsync(Token);
        return new CardRenderer(probe, options);
    }

    [GeneratedRegex(@"\A\d{4}-\d{2}-\d{2}\z")]
    private static partial Regex Day();
}
