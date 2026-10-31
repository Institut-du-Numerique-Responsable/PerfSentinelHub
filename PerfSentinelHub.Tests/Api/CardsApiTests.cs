using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Access;
using PerfSentinelHub.Api;
using PerfSentinelHub.Cards;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;
using static PerfSentinelHub.Tests.Support.CardSeed;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The card routes on Wednesday 2026-11-04, sources a (prod-eu) and b (prod-us). Access is off except
///     where a test turns it on, so the caller is the proxy header; the key files sit beside a database
///     inside this class's own directory.
/// </summary>
public sealed class CardsApiTests : IDisposable
{
    private const string OptionsPath = "/api/cards/options";
    private const string RenderPath = "/api/cards/render";
    private const string AttachmentPath = "/api/cards/attachment";
    private const string RotatePath = "/api/cards/keys/rotate";
    private const string Busy = "Another card is being made. Try again in a few seconds.";
    private const string N1 = "n_plus_one_sql:order-svc:0000000000000000000000000000000000000001";
    private const int Done = 20741; // 2026-10-15
    private const string Collective = """{"angle":"collective","period":"month","at":"2026-10-01"}""";

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["reader"] = """{"sub":"u-reader","email":"reader@example.internal","groups":["/readers"]}""",
        // A member at prod-eu, a reader only at prod-us.
        ["split"] = """{"sub":"u-split","email":"split@example.internal","groups":["/split"]}""",
        // cards.write everywhere, findings.read nowhere.
        ["storyteller"] = """{"sub":"u-story","email":"story@example.internal","groups":["/storytellers"]}"""
    };

    private readonly List<WebApplicationFactory<Program>> _hubs = [];

    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"perf-sentinel-hub-capi-{Guid.NewGuid():N}");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string DatabasePath => Path.Combine(_workspace, "data", "hub.db");

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
        TestPool.ClearFor(DatabasePath);
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, true);
    }

    // ------------------------------------------------------------ the guard

    [Theory]
    [InlineData("reader", "a", "You may not create cards.")]
    [InlineData("split", "b", "You may not create cards for these sources.")]
    [InlineData("storyteller", "a", "No grant gives you findings.read on this Hub.")]
    public async Task Rendering_needs_cards_write_and_findings_read(string user, string sourceId, string detail)
    {
        await using var provider = await FakeProvider.StartAsync(Users);
        var hub = AccessHub(provider);
        using var client = FakeProvider.Client(hub);
        using var callback = await FakeProvider.SignInAsync(client, user);
        using var request = Send("POST", RenderPath,
            $$"""{"angle":"collective","period":"month","at":"2026-01-01","source_id":"{{sourceId}}"}""",
            "same-origin");
        request.Headers.Remove("X-Forwarded-User");
        request.Headers.Add("Cookie", FakeProvider.SessionCookie(callback));

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(detail, await DetailAsync(response));
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "card-args.txt")));
    }

    [Theory]
    [InlineData("GET", OptionsPath + "?period=month")]
    [InlineData("POST", RenderPath)]
    [InlineData("POST", AttachmentPath)]
    public async Task A_card_answers_404_while_cards_are_off(string method, string path)
    {
        using var client = Hub(organisation: null).CreateClient();

        using var response = await client.SendAsync(Send(method, path, Collective), Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_card_needs_an_identity()
    {
        using var client = Hub().CreateClient();
        using var request = Send("POST", RenderPath, Collective);
        request.Headers.Remove("X-Forwarded-User");

        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The Hub does not know who is asking, and a card is made in somebody's name.",
            await DetailAsync(response));
    }

    [Theory]
    [InlineData(RenderPath, "card-args.txt")]
    [InlineData(AttachmentPath, "verify-args.txt")]
    public async Task A_cross_site_render_or_attachment_is_refused(string path, string engineCall)
    {
        using var client = Hub().CreateClient();

        using var response = await client.SendAsync(Send("POST", path, Collective, "cross-site"), Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("A request from another site is refused.", await DetailAsync(response));
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", engineCall)));
    }

    [Fact]
    public async Task An_engine_without_card_is_a_503()
    {
        using var client = Hub(new FakeEngine()).CreateClient();

        using var response = await client.SendAsync(Send("POST", RenderPath, Collective), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "This Hub's engine cannot render cards: it needs a perf-sentinel release with the card subcommand.",
            await DetailAsync(response));
        // The screen tells this 503 from a busy gate by the header alone.
        Assert.Null(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_body_too_large_or_a_free_figure_or_an_unknown_scope_is_refused_before_the_engine()
    {
        using var client = Hub().CreateClient();

        using var large = await client.SendAsync(
            Send("POST", RenderPath, $$"""{"angle":"curve","period":"month","title":"{{new string('t', 4096)}}"}"""),
            Token);
        using var figures = await client.SendAsync(
            Send("POST", RenderPath, """{"angle":"curve","period":"month","figures":{"avoidable_ops":1}}"""), Token);
        using var unknown = await client.SendAsync(
            Send("POST", RenderPath, """{"angle":"curve","period":"month","environment":"staging"}"""), Token);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, figures.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("Unknown environment or source_id.", await DetailAsync(unknown));
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "card-args.txt")));
    }

    // ------------------------------------------------------------ render

    [Fact]
    public async Task A_png_card_downloads_with_its_headers()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", RenderPath, Collective), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("perf-sentinel-card-month-2026-10-01.png", response.Content.Headers.ContentDisposition!.FileName);
        byte[] png = [0x89, .. "PNG fake"u8];
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task A_pdf_card_downloads_as_a_pdf()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", RenderPath,
            """{"angle":"story","period":"month","at":"2026-10-01","format":"pdf","lang":"fr"}"""), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("perf-sentinel-card-month-2026-10-01.pdf", response.Content.Headers.ContentDisposition!.FileName);
    }

    // Access on is where the Hub adds its own policy to every response, which the SVG's must win against.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_svg_card_downloads_as_an_attachment_with_its_headers(bool access)
    {
        await using var provider = await FakeProvider.StartAsync(Users);
        var hub = access ? AccessHub(provider, true) : Hub();
        await OneFixAsync(hub);

        using var response = await RenderAsync(hub,
            """{"angle":"collective","period":"month","at":"2026-10-01","source_id":"a","format":"svg"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment; filename=perf-sentinel-card-month-2026-10-01.svg",
            Assert.Single(response.Content.Headers.GetValues("Content-Disposition")), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("default-src 'none'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(FakeEngine.CardSvg, await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task A_png_card_keeps_the_global_policy()
    {
        await using var provider = await FakeProvider.StartAsync(Users);
        var hub = AccessHub(provider, true);
        await OneFixAsync(hub);

        using var response = await RenderAsync(hub,
            """{"angle":"collective","period":"month","at":"2026-10-01","source_id":"a"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("frame-ancestors 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    // Access on, so the global policy is there to keep: a JPEG XL is an image like the PNG.
    [Fact]
    public async Task A_jxl_card_downloads_as_an_attachment_with_its_headers()
    {
        await using var provider = await FakeProvider.StartAsync(Users);
        var hub = AccessHub(provider, true);
        await OneFixAsync(hub);

        using var response = await RenderAsync(hub,
            """{"angle":"collective","period":"month","at":"2026-10-01","source_id":"a","format":"jxl"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jxl", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment; filename=perf-sentinel-card-month-2026-10-01.jxl",
            Assert.Single(response.Content.Headers.GetValues("Content-Disposition")), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("frame-ancestors 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(FakeEngine.CardJxl, await response.Content.ReadAsByteArrayAsync(Token));
    }

    [Fact]
    public async Task The_first_card_creates_the_key()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", RenderPath, Collective), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = Assert.Single(await Database(hub).ReadCardKeysAsync(Token));
        Assert.Equal("ps1:" + new string('A', 22), key.Fingerprint);
        Assert.True(File.Exists(Path.Combine(_workspace, "data", "card-keys", new string('A', 22) + ".pk8")));
    }

    [Fact]
    public async Task An_incomplete_period_is_a_422_with_its_sentence()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await new CardSeed(Database(hub)).LedgerAsync("a", Sealed(October, day => day.Day == 20730 ? null : day));

        using var response = await client.SendAsync(Send("POST", RenderPath, Collective), Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(CardRules.Explain("uncovered_days", October), await DetailAsync(response));
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "card-args.txt")));
    }

    [Fact]
    public async Task A_curve_whose_previous_period_is_incomplete_is_a_422_naming_both_periods()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await new CardSeed(Database(hub)).LedgerAsync("a",
            Sealed(October, day => day with { AvoidableIoOps = 1, TotalIoOps = 10 }));

        using var response = await client.SendAsync(
            Send("POST", RenderPath, """{"angle":"curve","period":"month","at":"2026-10-01"}"""), Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var detail = await DetailAsync(response);
        Assert.Equal(CardRules.Explain("previous_uncovered_days", October), detail);
        Assert.Contains("October 2026", detail, StringComparison.Ordinal);
        Assert.Contains("September 2026", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("free.team", "team", "The card's font cannot draw a character of this field. Remove it, or write it another way.")]
    [InlineData("free.title", "title", "The card's font cannot draw a character of this field. Remove it, or write it another way.")]
    [InlineData("org.name", null,
        "The card's font cannot draw a character of the organisation's name. Ask an administrator to change Hub:Cards:Organisation.")]
    public async Task Unsupported_characters_is_a_422_naming_its_field(string engineField, string? field, string detail)
    {
        var hub = Hub(new FakeEngine
        {
            Card = true,
            CardRenderExitCode = 5,
            CardRenderOutput = $$"""{"result":"unrenderable","reason":"unsupported_characters","field":"{{engineField}}"}"""
        });
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", RenderPath, Collective), Token);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Equal(detail, body.RootElement.GetProperty("detail").GetString());
        Assert.Equal(field, body.RootElement.TryGetProperty("field", out var named) ? named.GetString() : null);
    }

    [Theory]
    [InlineData(5, "compact_too_large", HttpStatusCode.UnprocessableEntity,
        "The card's QR code cannot hold this organisation's domain. Ask an administrator for a shorter Hub:Cards:Domain.")]
    [InlineData(1, "unsupported_language", HttpStatusCode.UnprocessableEntity,
        "This Hub's engine cannot print cards in this language yet.")]
    [InlineData(3, "malformed_input", HttpStatusCode.BadGateway, "The engine could not render this card.")]
    public async Task A_qr_too_dense_is_a_422_naming_the_domain_setting(int exitCode, string reason,
        HttpStatusCode status, string detail)
    {
        var hub = Hub(new FakeEngine
        {
            Card = true,
            CardRenderExitCode = exitCode,
            CardRenderOutput = $$"""{"result":"unrenderable","reason":"{{reason}}"}"""
        });
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", RenderPath, Collective), Token);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(detail, await DetailAsync(response));
    }

    // ------------------------------------------------------------ options

    [Fact]
    public async Task Options_list_the_confirmed_fixes_with_their_labels()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);
        await Database(hub).SetEndpointLabelAsync("order-svc", "GET /orders", "Order list", "alice", Token);

        using var response = await client.SendAsync(
            Send("GET", OptionsPath + "?period=month&at=2026-10-01&environment=prod"), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = body.RootElement;
        var fix = Assert.Single(root.GetProperty("fixes").EnumerateArray());
        Assert.Equal(N1, fix.GetProperty("signature").GetString());
        Assert.Equal("Order list", fix.GetProperty("label").GetString());
        Assert.Equal(N1, root.GetProperty("default_signature").GetString());
        Assert.Equal("October 2026", root.GetProperty("period_name").GetString());
        Assert.Equal(["prod-eu"], root.GetProperty("sources").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Acme", root.GetProperty("org").GetProperty("name").GetString());
        Assert.False(File.Exists(Path.Combine(_workspace, "reports", "card-args.txt")));
    }

    [Fact]
    public async Task Options_of_a_period_still_running_carry_its_refusal()
    {
        using var client = Hub().CreateClient();

        using var open = await client.SendAsync(Send("GET", OptionsPath + "?period=month&at=2026-11-02"), Token);
        using var invalid = await client.SendAsync(Send("GET", OptionsPath + "?period=day"), Token);

        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        using var body = JsonDocument.Parse(await open.Content.ReadAsStringAsync(Token));
        Assert.Equal("open_period", body.RootElement.GetProperty("refusal").GetString());
        Assert.Equal("A card covers a closed period. Pick an earlier one.",
            body.RootElement.GetProperty("refusal_text").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    // ------------------------------------------------------------ attachment

    [Fact]
    public async Task Attachment_without_a_domain_is_a_409()
    {
        using var client = Hub(domain: null).CreateClient();

        using var response = await client.SendAsync(Send("POST", AttachmentPath, Collective), Token);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Set Hub:Cards:Domain to check an attachment.", await DetailAsync(response));
    }

    [Fact]
    public async Task Attachment_names_what_it_contacted()
    {
        var hub = Hub(new FakeEngine
        {
            Card = true,
            VerifyCardExitCode = 4,
            VerifyCardOutput =
                """{"integrity":"ok","content":"intact","level":1,"domain":"example.com","key":"unknown","reason":"binding_check_failed"}"""
        });
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(
            Send("POST", AttachmentPath, """{"angle":"collective","period":"month","at":"2026-10-01","format":"pdf"}"""),
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = body.RootElement;
        Assert.Equal(["cloudflare-dns.com", "example.com"],
            root.GetProperty("contacted").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(1, root.GetProperty("level").GetInt32());
        Assert.Equal(("ok", "unknown", "binding_check_failed"),
            (root.GetProperty("integrity").GetString(), root.GetProperty("key").GetString(),
                root.GetProperty("reason").GetString()));
        var arguments = await File.ReadAllTextAsync(Path.Combine(_workspace, "reports", "card-args.txt"), Token);
        Assert.Contains("--format png", arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("svg")]
    [InlineData("jxl")]
    public async Task The_attachment_check_renders_a_png_whatever_the_format(string format)
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", AttachmentPath,
            $$"""{"angle":"collective","period":"month","at":"2026-10-01","format":"{{format}}"}"""), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var arguments = await File.ReadAllTextAsync(Path.Combine(_workspace, "reports", "card-args.txt"), Token);
        Assert.Contains("--format png", arguments, StringComparison.Ordinal);
        Assert.DoesNotContain($"--format {format}", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_modified_file_reaches_the_screen()
    {
        var hub = Hub(new FakeEngine
        {
            Card = true,
            VerifyCardExitCode = 1,
            VerifyCardOutput =
                """{"integrity":"ok","content":"modified","level":1,"domain":null,"key":"unknown","reason":"content_modified","figures":null}"""
        });
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", AttachmentPath, Collective), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var root = body.RootElement;
        Assert.Equal(("modified", "content_modified"),
            (root.GetProperty("content").GetString(), root.GetProperty("reason").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("figures").ValueKind);
    }

    [Fact]
    public async Task Attachment_answers_the_signed_figures_by_the_engine_names()
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", AttachmentPath, Collective), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var figures = body.RootElement.GetProperty("figures");
        Assert.Equal("intact", body.RootElement.GetProperty("content").GetString());
        Assert.Equal(("story", "month", "2026-07-01", 41, 1937000),
            (figures.GetProperty("angle").GetString(), figures.GetProperty("period").GetProperty("kind").GetString(),
                figures.GetProperty("before_fix_period").GetProperty("from").GetString(),
                figures.GetProperty("cited_ops").GetInt32(), figures.GetProperty("before_fix_ops").GetInt32()));
    }

    [Fact]
    public async Task Attachment_without_a_verdict_is_a_502()
    {
        var hub = Hub(new FakeEngine { Card = true, VerifyCardExitCode = 3, VerifyCardOutput = "{}" });
        using var client = hub.CreateClient();
        await OneFixAsync(hub);

        using var response = await client.SendAsync(Send("POST", AttachmentPath, Collective), Token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    // ------------------------------------------------------------ the render gate

    [Theory]
    [InlineData(RenderPath)]
    [InlineData(AttachmentPath)]
    [InlineData(RotatePath)]
    public async Task A_third_card_render_while_two_run_is_a_503_with_retry_after(string path)
    {
        var hub = Hub();
        using var client = hub.CreateClient();
        await OneFixAsync(hub);
        var body = path == RotatePath ? "{}" : Collective;
        var gate = hub.Services.GetRequiredService<CardRenderGate>();

        var entered = Enumerable.Range(0, 2).Count(_ => gate.TryEnter());
        try
        {
            Assert.Equal(2, entered);

            using var refused = await client.SendAsync(Send("POST", path, body), Token);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("5", refused.Headers.RetryAfter?.ToString());
            Assert.Equal(Busy, await DetailAsync(refused));
            Assert.Empty(EngineRuns());
            Assert.Empty(await Database(hub).ReadCardKeysAsync(Token));
        }
        finally
        {
            for (var slot = 0; slot < entered; slot++)
                gate.Exit();
        }

        using var accepted = await client.SendAsync(Send("POST", path, body), Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task The_render_gate_follows_its_setting()
    {
        var hub = Hub(configure: builder => builder.UseSetting("Hub:Cards:MaxConcurrentRenders", "1"));
        using var client = hub.CreateClient();
        await OneFixAsync(hub);
        var gate = hub.Services.GetRequiredService<CardRenderGate>();

        Assert.True(gate.TryEnter());
        try
        {
            using var refused = await client.SendAsync(Send("POST", RenderPath, Collective), Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        }
        finally
        {
            gate.Exit();
        }

        Assert.Equal(1, FreeSlots(gate, 3));
    }

    [Fact]
    public async Task A_request_refused_before_the_engine_takes_no_slot()
    {
        var hub = Hub();
        var off = Hub(organisation: null);
        using var client = hub.CreateClient();
        using var offClient = off.CreateClient();
        await new CardSeed(Database(hub)).LedgerAsync("a", Sealed(October, day => day.Day == 20730 ? null : day));
        CardRenderGate[] gates = [.. new[] { hub, off }.Select(each => each.Services.GetRequiredService<CardRenderGate>())];

        var entered = gates.Select(gate => Enumerable.Range(0, 2).Count(_ => gate.TryEnter())).ToList();
        try
        {
            using var figures = await client.SendAsync(
                Send("POST", RenderPath, """{"angle":"curve","period":"month","figures":{"avoidable_ops":1}}"""), Token);
            using var incomplete = await client.SendAsync(Send("POST", RenderPath, Collective), Token);
            using var cardsOff = await offClient.SendAsync(Send("POST", RenderPath, Collective), Token);

            Assert.Equal(HttpStatusCode.BadRequest, figures.StatusCode);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, cardsOff.StatusCode);
        }
        finally
        {
            for (var each = 0; each < gates.Length; each++)
            {
                for (var slot = 0; slot < entered[each]; slot++)
                    gates[each].Exit();
            }
        }
    }

    [Fact]
    public async Task A_failed_or_timed_out_render_frees_its_slot()
    {
        // The keygen failure first: the hubs share one database, and the first key made would be reused.
        (FakeEngine Engine, string Path, HttpStatusCode Status)[] cases =
        [
            (new FakeEngine { Card = true, CardKeygenExitCode = 1 }, RenderPath, HttpStatusCode.BadGateway),
            (new FakeEngine
            {
                Card = true,
                CardRenderExitCode = 5,
                CardRenderOutput = """{"result":"unrenderable","reason":"unsupported_characters","field":"free.team"}"""
            }, RenderPath, HttpStatusCode.UnprocessableEntity),
            (new FakeEngine { Card = true, SleepSeconds = 5 }, RenderPath, HttpStatusCode.BadGateway),
            (new FakeEngine { Card = true, VerifyCardExitCode = 3, VerifyCardOutput = "{}" }, AttachmentPath,
                HttpStatusCode.BadGateway)
        ];

        foreach (var (engine, path, status) in cases)
        {
            var hub = Hub(engine);
            using var client = hub.CreateClient();
            await OneFixAsync(hub);
            hub.Services.GetRequiredService<CardRenderer>().Timeout = TimeSpan.FromMilliseconds(300);

            using var response = await client.SendAsync(Send("POST", path, Collective), Token);

            Assert.Equal(status, response.StatusCode);
            Assert.Equal(2, FreeSlots(hub.Services.GetRequiredService<CardRenderGate>(), 2));
        }
    }

    // ------------------------------------------------------------ helpers

    // Takes every free slot, counts them and hands them back.
    private static int FreeSlots(CardRenderGate gate, int tries)
    {
        var free = Enumerable.Range(0, tries).Count(_ => gate.TryEnter());
        for (var slot = 0; slot < free; slot++)
            gate.Exit();
        return free;
    }

    // The argument files the engine's card subcommands append to, in the reports directory they run in.
    private static readonly string[] EngineRunFiles = ["card-args.txt", "card-keygen-args.txt", "verify-args.txt"];

    private string[] EngineRuns()
    {
        return [.. EngineRunFiles.Where(name => File.Exists(Path.Combine(_workspace, "reports", name)))];
    }

    private static HubDatabase Database(WebApplicationFactory<Program> hub)
    {
        return hub.Services.GetRequiredService<HubDatabase>();
    }

    // October whole at prod-eu, one n+1 fix confirmed there.
    private static async Task OneFixAsync(WebApplicationFactory<Program> hub)
    {
        var seed = new CardSeed(Database(hub));
        await seed.LedgerAsync("a", Sealed(October));
        await seed.FixAsync("a", N1, Done);
    }

    private WebApplicationFactory<Program> Hub(
        FakeEngine? engine = null,
        string? organisation = "Acme",
        string? domain = "example.com",
        Action<IWebHostBuilder>? configure = null)
    {
        var enginePath = ((engine ?? new FakeEngine { Card = true }) with { Version = "perf-sentinel 0.39.0" })
            .WriteTo(Path.Combine(_workspace, $"engine-{Guid.NewGuid():N}"));
        var hub = new HubApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(
                    new FakeTimeProvider(DateTimeOffset.Parse(Now, CultureInfo.InvariantCulture)));
                services.PostConfigure<HubOptions>(options =>
                {
                    options.DatabasePath = DatabasePath;
                    options.Sources = [.. Sources];
                    options.AckRelay = new AckRelayOptions { TrustIdentityHeader = true };
                    options.Analysis = new AnalysisOptions
                    {
                        EngineBinaryPath = enginePath,
                        ReportDirectory = Path.Combine(_workspace, "reports")
                    };
                    // A with, so a test's Hub:Cards:MaxConcurrentRenders setting stays bound.
                    options.Cards = options.Cards with
                    {
                        Organisation = organisation,
                        Domain = organisation is null ? null : domain
                    };
                });
            });
            configure?.Invoke(builder);
        });
        _hubs.Add(hub);
        return hub;
    }

    // seededClock keeps the seeded clock for a render, the session signed in by SessionOnSeededClockAsync.
    private WebApplicationFactory<Program> AccessHub(FakeProvider provider, bool seededClock = false)
    {
        return Hub(configure: builder =>
        {
            provider.Configure(builder);
            builder.UseSetting("Hub:Access:Enabled", "true");
            builder.UseSetting("Hub:MetricsPort", "9464");
            builder.ConfigureServices(services =>
            {
                // The client's cookie jar reads today's clock; the guard refuses before any figure is read.
                if (!seededClock)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(TimeProvider.System);
                }

                services.PostConfigure<HubOptions>(options =>
                {
                    // Access on names every caller itself, so the proxy header is off.
                    options.AckRelay = new AckRelayOptions();
                    options.Access = new AccessOptions
                    {
                        Enabled = true,
                        Admins = ["u-admin"],
                        Projects =
                        [
                            new ProjectOptions { Name = "eu", Patterns = [new PatternOptions { Source = "a" }] },
                            new ProjectOptions { Name = "us", Patterns = [new PatternOptions { Source = "b" }] }
                        ],
                        Roles = [new RoleOptions { Name = "storyteller", Permissions = ["cards.write"] }],
                        Grants =
                        [
                            new GrantOptions { Group = "/storytellers", Project = "*", Role = "storyteller" },
                            new GrantOptions { Group = "/readers", Project = "*", Role = "reader" },
                            new GrantOptions { Group = "/split", Project = "eu", Role = "member" },
                            new GrantOptions { Group = "/split", Project = "us", Role = "reader" }
                        ]
                    };
                });
            });
        });
    }

    private static HttpRequestMessage Send(string method, string path, string? body = null, string? site = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null && method != "GET")
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("X-Forwarded-User", "alice");
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        return request;
    }

    // A render from the Hub's page: split's session when access is on, the proxy's alice when it is off.
    private static async Task<HttpResponseMessage> RenderAsync(WebApplicationFactory<Program> hub, string body)
    {
        using var client = FakeProvider.Client(hub);
        using var request = Send("POST", RenderPath, body, "same-origin");
        if (hub.Services.GetRequiredService<AccessPolicy>().Enabled)
        {
            request.Headers.Remove("X-Forwarded-User");
            request.Headers.Add("Cookie", await SessionOnSeededClockAsync(hub, "split"));
        }

        return await client.SendAsync(request, Token);
    }

    // The sign-in's cookies go by hand: the client's own jar would judge their expiry against today.
    private static async Task<string> SessionOnSeededClockAsync(WebApplicationFactory<Program> hub, string user)
    {
        using var client = hub.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false
        });
        using var challenge = await client.GetAsync("/", Token);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        using var callback = new HttpRequestMessage(HttpMethod.Get,
            $"/auth/callback?code={user}&state={Uri.EscapeDataString(state!)}");
        callback.Headers.Add("Cookie",
            string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(cookie => cookie.Split(';')[0])));
        using var signedIn = await client.SendAsync(callback, Token);
        return FakeProvider.SessionCookie(signedIn);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.GetProperty("detail").GetString();
    }
}
