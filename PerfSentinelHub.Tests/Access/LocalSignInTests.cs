using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Access;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests.Access;

/// <summary>
///     Local break-glass accounts and the Hub's own sign-in page. Every request reaches the
///     Hub from 127.0.0.1, the trusted proxy, with the client's address in X-Forwarded-For.
/// </summary>
public sealed class LocalSignInTests : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string Here = "203.0.113.7";
    private const string Elsewhere = "198.51.100.1";

    // Hashed once: 600000 iterations is the point, and it costs a few hundred milliseconds.
    private static readonly string Hash = LocalPassword.Hash(Password);

    private static readonly Dictionary<string, string> Users = new(StringComparer.Ordinal)
    {
        ["member"] = """{"sub":"u-member","email":"member@example.internal","groups":["/teams/shop"]}"""
    };

    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly HubApplicationFactory _hub = new();
    private readonly ListLogger<Program> _log = new();
    private FakeProvider _provider = null!;

    private WebApplicationFactory<Program> Factory { get; set; } = null!;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync(Users);
        Factory = Hub(_hub, Hash);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
    }

    // ------------------------------------------------------------ the page

    [Fact]
    public async Task A_page_goes_to_the_Hub_sign_in_page()
    {
        using var client = Client(Here);

        using var response = await client.GetAsync("/app.js?v=1", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("/auth/sign-in", location.AbsolutePath);
        Assert.Equal("/app.js?v=1", QueryHelpers.ParseQuery(location.Query)["return_url"]);
    }

    [Fact]
    public async Task The_page_renders_while_the_provider_is_unreachable_and_offers_single_sign_on_first()
    {
        // Nothing here contacts the provider: it is gone before the page is asked for.
        await _provider.DisposeAsync();
        using var client = Client(Here);

        using var response = await client.GetAsync("/auth/sign-in?return_url=%2Fdevice", Cancel);
        var page = await response.Content.ReadAsStringAsync(Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        AssertPageHeaders(response, page);
        Assert.Contains("<title>Sign in · Perf Sentinel Hub</title>", page, StringComparison.Ordinal);
        Assert.Contains("href=\"/auth/sign-in/sso?return_url=%2Fdevice\"", page, StringComparison.Ordinal);
        Assert.Contains("<details>", page, StringComparison.Ordinal);
        Assert.Contains("value=\"/device\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.True(page.IndexOf("Sign in with single sign-on", StringComparison.Ordinal) <
                    page.IndexOf("Sign in with a local account", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Single_sign_on_goes_to_the_provider_and_comes_back_to_the_return_url()
    {
        using var client = Client(Here);

        using var sso = await client.GetAsync("/auth/sign-in/sso?return_url=%2Fapp.js%3Fv%3D1", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, sso.StatusCode);
        Assert.StartsWith("https://idp.example.internal/authorize?", sso.Headers.Location!.ToString(),
            StringComparison.Ordinal);
        var state = QueryHelpers.ParseQuery(sso.Headers.Location.Query)["state"];
        using var callback = await client.GetAsync(
            $"/auth/callback?code=member&state={Uri.EscapeDataString(state!)}", Cancel);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/app.js?v=1", callback.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/findings")]
    public async Task The_API_still_answers_401(string path)
    {
        using var client = Client(Here);

        using var response = await client.GetAsync(path, Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_report_inside_the_launcher_frame_answers_401_and_one_opened_alone_goes_to_the_page()
    {
        using var client = Client(Here);
        using var framed = new HttpRequestMessage(HttpMethod.Get, "/reports/0123456789abcdef.html");
        framed.Headers.Add("Sec-Fetch-Dest", "iframe");

        using var inFrame = await client.SendAsync(framed, Cancel);
        using var alone = await client.GetAsync("/reports/0123456789abcdef.html", Cancel);

        Assert.Equal(HttpStatusCode.Unauthorized, inFrame.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, alone.StatusCode);
        Assert.Equal("/auth/sign-in", alone.Headers.Location!.AbsolutePath);
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/")]
    [InlineData("")]
    public async Task A_foreign_return_url_falls_back_to_the_root(string returnUrl)
    {
        using var client = Client(Here);

        using var page = await client.GetAsync("/auth/sign-in?return_url=" + Uri.EscapeDataString(returnUrl), Cancel);
        using var signIn = await SignInAsync(client, "ops", Password, returnUrl);

        Assert.Contains("href=\"/auth/sign-in/sso?return_url=%2F\"", await page.Content.ReadAsStringAsync(Cancel),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/", signIn.Headers.Location!.OriginalString);
    }

    // ------------------------------------------------------------ signing in

    [Fact]
    public async Task A_local_account_signs_in_as_local_name_reaches_its_internal_groups_and_is_logged()
    {
        using var client = Client(Here);

        using var signIn = await SignInAsync(client, "OPS", Password, "/#/access");
        using var access = await client.GetAsync("/api/access", Cancel);
        var body = JsonDocument.Parse(await access.Content.ReadAsStringAsync(Cancel)).RootElement;

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal("/#/access", signIn.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        Assert.Equal("local:ops", body.GetProperty("identity").GetString());
        Assert.Equal("local:ops", body.GetProperty("subject").GetString());
        Assert.Equal("session", body.GetProperty("caller").GetString());
        Assert.True(body.GetProperty("admin").GetBoolean());
        Assert.Equal("""[{"name":"break-glass","origin":"internal"}]""", body.GetProperty("groups").GetRawText());
        Assert.Equal("""["ops"]""", body.GetProperty("configuration").GetProperty("local_accounts").GetRawText());
        Assert.Contains($"Local account ops signed in from {Here}.", _log.Messages);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_name_give_identical_status_and_bytes()
    {
        using var first = Client(Here);
        using var second = Client(Elsewhere);

        using var wrong = await SignInAsync(first, "ops", Password + "!");
        using var unknown = await SignInAsync(second, "nobody", Password);
        var wrongBytes = await wrong.Content.ReadAsByteArrayAsync(Cancel);

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(wrong.StatusCode, unknown.StatusCode);
        Assert.Equal(wrongBytes, await unknown.Content.ReadAsByteArrayAsync(Cancel));
        var page = Encoding.UTF8.GetString(wrongBytes);
        AssertPageHeaders(wrong, page);
        Assert.Contains("The name or the password is wrong.", page, StringComparison.Ordinal);
        Assert.Contains("<details open>", page, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("nobody", page, StringComparison.Ordinal);
        Assert.Contains($"Local sign-in refused for ops from {Here} after 1 consecutive failures.", _log.Messages);
        Assert.Contains($"Local sign-in refused for (unknown name) from {Elsewhere} after 1 consecutive failures.",
            _log.Messages);
        Assert.DoesNotContain(_log.Messages, message => message.Contains("nobody", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_name_costs_a_full_hash_like_a_wrong_password()
    {
        using var first = Client(Here);
        using var second = Client(Elsewhere);
        var wrong = TimeSpan.MaxValue;
        var unknown = TimeSpan.MaxValue;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            wrong = Min(wrong, await TimedAsync(first, "ops"));
            unknown = Min(unknown, await TimedAsync(second, "nobody"));
        }

        // Skipping the hash would answer in a few milliseconds against hundreds for one.
        Assert.True(unknown * 4 >= wrong, $"unknown name {unknown}, wrong password {wrong}");

        static TimeSpan Min(TimeSpan left, TimeSpan right)
        {
            return left < right ? left : right;
        }

        static async Task<TimeSpan> TimedAsync(HttpClient client, string name)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await SignInAsync(client, name, Password + "!");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            return Stopwatch.GetElapsedTime(started);
        }
    }

    [Fact]
    public async Task A_password_over_1024_characters_is_refused_even_when_its_hash_is_declared()
    {
        var tooLong = new string('p', LocalPassword.MaxLength + 1);
        await using var hub = Hub(_hub, LocalPassword.Hash(tooLong));
        using var client = Client(Here, hub);

        using var response = await SignInAsync(client, "ops", tooLong);
        using var wrong = await SignInAsync(client, "ops", Password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsByteArrayAsync(Cancel),
            await response.Content.ReadAsByteArrayAsync(Cancel));
    }

    [Fact]
    public async Task Five_failures_start_a_backoff_that_refuses_even_the_right_password()
    {
        using var client = Client(Here);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await SignInAsync(client, "ops", "wrong password, attempt " + attempt);
            Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        }

        using var throttled = await SignInAsync(client, "ops", Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), throttled.Headers.RetryAfter?.Delta);
        Assert.Contains("Too many failed attempts. Try again in 30 seconds.",
            await throttled.Content.ReadAsStringAsync(Cancel),
            StringComparison.Ordinal);

        // The backoff is over, the address's minute is not: still refused, without hashing.
        _clock.Advance(TimeSpan.FromSeconds(30));
        using var budget = await SignInAsync(client, "ops", Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, budget.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), budget.Headers.RetryAfter?.Delta);

        _clock.Advance(TimeSpan.FromSeconds(30));
        using var signIn = await SignInAsync(client, "ops", Password);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
    }

    [Fact]
    public async Task A_backoff_on_one_address_leaves_another_free()
    {
        using var attacker = Client(Here);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await SignInAsync(attacker, "ops", "wrong password, attempt " + attempt);
            Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        }

        using var operatorClient = Client(Elsewhere);
        using var throttled = await SignInAsync(attacker, "ops", Password);
        using var signIn = await SignInAsync(operatorClient, "ops", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
    }

    [Fact]
    public async Task An_unknown_name_is_throttled_like_a_declared_one()
    {
        using var client = Client(Here);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await SignInAsync(client, "nobody", Password);
            Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        }

        // A fresh minute: the budget is back, and only the name's backoff remains.
        _clock.Advance(TimeSpan.FromSeconds(60));
        using (var sixth = await SignInAsync(client, "nobody", Password))
        {
            Assert.Equal(HttpStatusCode.Forbidden, sixth.StatusCode);
        }

        using var seventh = await SignInAsync(client, "nobody", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, seventh.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), seventh.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task A_sixth_hashing_attempt_within_a_minute_from_one_address_under_six_names_gets_429()
    {
        using var client = Client(Here);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var failed = await SignInAsync(client, "name-" + attempt, Password);
            Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        }

        _clock.Advance(TimeSpan.FromSeconds(20));
        using var sixth = await SignInAsync(client, "ops", Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(40), sixth.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task A_flood_of_random_names_from_one_address_leaves_a_correct_sign_in_from_another_answering_302()
    {
        using var attacker = Client(Here);
        using var operatorClient = Client(Elsewhere);

        var flood = Enumerable.Range(0, 20)
            .Select(attempt => SignInAsync(attacker, Guid.NewGuid().ToString("N"), Password))
            .ToList();
        var signIn = SignInAsync(operatorClient, "ops", Password);
        var answers = await Task.WhenAll(flood);
        using var operatorAnswer = await signIn;

        Assert.Equal(HttpStatusCode.Redirect, operatorAnswer.StatusCode);
        Assert.Equal(5, answers.Count(answer => answer.StatusCode == HttpStatusCode.Forbidden));
        Assert.Equal(15, answers.Count(answer => answer.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var answer in answers) answer.Dispose();
    }

    [Theory]
    [InlineData("cross-site", null)]
    [InlineData("same-site", null)]
    [InlineData(null, "https://evil.example")]
    [InlineData("same-origin", "http://localhost")]
    public async Task A_cross_site_post_is_refused(string? site, string? origin)
    {
        using var client = Client(Here);

        using var response = await SignInAsync(client, "ops", Password, "/", site, origin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.DoesNotContain(response.Headers, header => header.Key == "Set-Cookie");
    }

    [Fact]
    public async Task The_same_origin_with_its_origin_header_signs_in()
    {
        using var client = Client(Here);

        using var response = await SignInAsync(client, "ops", Password, "/", "same-origin", "https://localhost");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Only_a_small_url_encoded_form_is_read()
    {
        using var client = Client(Here);
        using var json = new HttpRequestMessage(HttpMethod.Post, "/auth/sign-in/local")
        {
            Content = new StringContent("""{"name":"ops"}""", Encoding.UTF8, "application/json")
        };
        using var large = new HttpRequestMessage(HttpMethod.Post, "/auth/sign-in/local")
        {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("name", "ops"),
                new KeyValuePair<string, string>("password", new string('p', 4096))
            ])
        };

        using var unsupported = await client.SendAsync(json, Cancel);
        using var tooLarge = await client.SendAsync(large, Cancel);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }

    // ------------------------------------------------------------ the session

    [Fact]
    public async Task A_local_session_ends_after_eight_hours_however_it_is_used()
    {
        using var client = Client(Here);
        using (var signIn = await SignInAsync(client, "ops", Password))
        {
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        }

        _clock.Advance(TimeSpan.FromHours(5));
        using (var atFive = await client.GetAsync("/api/status", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, atFive.StatusCode);
            Assert.False(atFive.Headers.Contains("Set-Cookie"));
        }

        _clock.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(1));
        using var afterEight = await client.GetAsync("/api/status", Cancel);
        Assert.Equal(HttpStatusCode.Unauthorized, afterEight.StatusCode);
    }

    [Fact]
    public async Task A_rotated_hash_ends_the_session_after_restart()
    {
        var session = await LocalSessionAsync(Factory);

        // A restart with the same hash keeps the session, so the 401 below comes from the stamp.
        await using var same = Hub(_hub, Hash);
        await using var rotated = Hub(_hub, LocalPassword.Hash(Password + " rotated"));
        using var kept = await WithCookieAsync(same, session, "/api/status");
        using var ended = await WithCookieAsync(rotated, session, "/api/status");

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
    }

    [Fact]
    public async Task An_undeclared_account_s_session_is_rejected()
    {
        var session = await LocalSessionAsync(Factory);

        await using var same = Hub(_hub, Hash);
        await using var removed = Hub(_hub, Hash, "admin");
        using var kept = await WithCookieAsync(same, session, "/api/status");
        using var response = await WithCookieAsync(removed, session, "/api/status");

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_local_session_is_rejected_once_access_is_off()
    {
        var session = await LocalSessionAsync(Factory);

        await using var on = Factory.WithWebHostBuilder(builder => builder.UseSetting("Hub:Access:Enabled", "true"));
        await using var off = Factory.WithWebHostBuilder(builder => builder.UseSetting("Hub:Access:Enabled", "false"));
        using var kept = await WithCookieAsync(on, session, "/api/status");
        using var response = await WithCookieAsync(off, session, "/api/status");

        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task With_RequiredGroups_set_a_local_account_signs_in_and_its_session_is_kept()
    {
        await using var gated = Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Hub:Auth:RequiredGroups:0", "/teams/shop"));
        var session = await LocalSessionAsync(gated);

        using var response = await WithCookieAsync(gated, session, "/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Device_with_a_local_session_under_an_hour_goes_to_the_page_which_renders()
    {
        using var client = Client(Here);
        using (var signIn = await SignInAsync(client, "ops", Password))
        {
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        }

        _clock.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(1));

        using var device = await client.GetAsync("/device", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, device.StatusCode);
        Assert.Equal("/auth/sign-in", device.Headers.Location!.AbsolutePath);
        Assert.Equal("/device", QueryHelpers.ParseQuery(device.Headers.Location.Query)["return_url"]);

        // Signed in or not, the page renders: a redirect would loop with /device.
        using var page = await client.GetAsync(device.Headers.Location, Cancel);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    // ------------------------------------------------------------ without local accounts

    [Fact]
    public async Task Without_a_local_account_the_routes_are_unmapped_and_a_page_goes_to_the_provider()
    {
        await using var none = _hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            Access(builder, null, "ops");
        });
        using var client = FakeProvider.Client(none);

        using var root = await client.GetAsync("/", Cancel);
        using var page = await client.GetAsync("/auth/sign-in", Cancel);

        Assert.StartsWith("https://idp.example.internal/authorize?", root.Headers.Location!.ToString(),
            StringComparison.Ordinal);
        Assert.StartsWith("https://idp.example.internal/authorize?", page.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_access_off_a_declared_local_account_is_ignored()
    {
        await using var off = Factory.WithWebHostBuilder(builder => builder.UseSetting("Hub:Access:Enabled", "false"));
        using var client = FakeProvider.Client(off);

        using var root = await client.GetAsync("/", Cancel);
        using var signIn = await SignInAsync(client, "ops", Password);

        Assert.StartsWith("https://idp.example.internal/authorize?", root.Headers.Location!.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.StartsWith("https://idp.example.internal/authorize?", signIn.Headers.Location!.ToString(),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the gate and the throttle

    [Fact]
    public async Task A_full_gate_answers_503_on_the_page_and_gives_the_attempt_back()
    {
        var gate = Factory.Services.GetRequiredService<LocalSignInGate>();
        var throttle = Factory.Services.GetRequiredService<LocalSignInThrottle>();
        Assert.True(await gate.TryEnterAsync(TimeSpan.FromSeconds(10), Cancel));
        var waiting = Enumerable.Range(0, LocalSignInGate.MaxWaiting)
            .Select(_ => gate.TryEnterAsync(TimeSpan.FromSeconds(30), Cancel))
            .ToList();
        using var client = Client(Here);

        using var response = await SignInAsync(client, "ops", Password);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Contains("role=\"alert\">The Hub is busy. Try again in a moment.</p>",
            await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        Assert.Equal(0, throttle.Failures($"{Here}\nops"));

        foreach (var _ in waiting) gate.Exit();
        Assert.All(await Task.WhenAll(waiting), Assert.True);
        gate.Exit();
    }

    [Fact]
    public async Task The_gate_holds_one_attempt_and_refuses_a_ninth_waiter_at_once()
    {
        using var gate = new LocalSignInGate();
        Assert.True(await gate.TryEnterAsync(TimeSpan.FromSeconds(10), Cancel));
        var waiting = Enumerable.Range(0, LocalSignInGate.MaxWaiting)
            .Select(_ => gate.TryEnterAsync(TimeSpan.FromSeconds(10), Cancel))
            .ToList();

        Assert.False(await gate.TryEnterAsync(TimeSpan.FromSeconds(10), Cancel));

        foreach (var _ in waiting) gate.Exit();
        Assert.All(await Task.WhenAll(waiting), Assert.True);
    }

    [Fact]
    public void The_backoff_doubles_from_thirty_seconds_to_fifteen_minutes()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var throttle = new LocalSignInThrottle(clock);
        int[] waits = [30, 60, 120, 240, 480, 900, 900];
        for (var failure = 0; failure < 5; failure++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Null(throttle.TryReserve("a", "a\nops"));
        }

        foreach (var wait in waits)
        {
            Assert.Equal(TimeSpan.FromSeconds(wait), throttle.TryReserve("a", "a\nops"));
            clock.Advance(TimeSpan.FromSeconds(wait));
            Assert.Null(throttle.TryReserve("a", "a\nops"));
        }

        throttle.Succeeded("a\nops");
        Assert.Equal(0, throttle.Failures("a\nops"));
    }

    [Fact]
    public void A_reservation_given_back_counts_neither_against_the_budget_nor_as_a_failure()
    {
        var throttle = new LocalSignInThrottle(new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1)));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Null(throttle.TryReserve("a", "a\nops"));
            throttle.GiveBack("a", "a\nops");
        }

        Assert.Equal(0, throttle.Failures("a\nops"));
        Assert.Null(throttle.TryReserve("a", "a\nops"));
        Assert.Equal(1, throttle.Failures("a\nops"));
    }

    [Fact]
    public void Each_table_keeps_at_most_ten_thousand_keys_dropping_the_least_recently_used()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddDays(1));
        var throttle = new LocalSignInThrottle(clock);
        Assert.Null(throttle.TryReserve("idle", "idle\nops"));
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Null(throttle.TryReserve("a1", "a1\nops"));
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var key = 2; key < LocalSignInThrottle.MaxKeys; key++)
            Assert.Null(throttle.TryReserve("a" + key, "a" + key + "\nops"));

        Assert.Null(throttle.TryReserve("new", "new\nops"));
        Assert.Equal(0, throttle.Failures("idle\nops"));
        Assert.Equal(1, throttle.Failures("a1\nops"));

        Assert.Null(throttle.TryReserve("newer", "newer\nops"));
        Assert.Equal(0, throttle.Failures("a1\nops"));
        Assert.Equal(1, throttle.Failures("a2\nops"));
        Assert.Equal(1, throttle.Failures("newer\nops"));

        // A key in use moves to the back: a3 goes before a2.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(throttle.TryReserve("a2", "a2\nops"));
        Assert.Null(throttle.TryReserve("newest", "newest\nops"));
        Assert.Equal(2, throttle.Failures("a2\nops"));
        Assert.Equal(0, throttle.Failures("a3\nops"));
    }

    // ------------------------------------------------------------ helpers

    private WebApplicationFactory<Program> Hub(WebApplicationFactory<Program> hub, string hash, string account = "ops")
    {
        return hub.WithWebHostBuilder(builder =>
        {
            _provider.Configure(builder);
            Access(builder, hash, account);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<IStartupFilter, FromTheProxy>();
                services.AddSingleton<ILoggerProvider>(new Capture(_log));
            });
        });
    }

    private static void Access(IWebHostBuilder builder, string? hash, string account)
    {
        builder.UseSetting("Hub:Access:Enabled", "true");
        builder.UseSetting("Hub:MetricsPort", "9464");
        builder.UseSetting("Hub:Auth:TrustedProxyNetworks:0", "127.0.0.1/32");
        builder.UseSetting("Hub:Access:AdminGroups:0", "internal:break-glass");
        builder.UseSetting("Hub:Access:Groups:0:Name", "break-glass");
        builder.UseSetting("Hub:Access:Grants:0:Group", "/teams/shop");
        builder.UseSetting("Hub:Access:Grants:0:Project", "*");
        builder.UseSetting("Hub:Access:Grants:0:Role", "member");
        if (hash is null) return;
        builder.UseSetting("Hub:Access:Groups:0:Members:0", "local:" + account);
        builder.UseSetting("Hub:Access:LocalAccounts:0:Name", account);
        builder.UseSetting("Hub:Access:LocalAccounts:0:PasswordHash", hash + "\n");
    }

    private HttpClient Client(string address, WebApplicationFactory<Program>? hub = null)
    {
        var client = FakeProvider.Client(hub ?? Factory);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        return client;
    }

    private static Task<HttpResponseMessage> SignInAsync(
        HttpClient client,
        string name,
        string password,
        string returnUrl = "/",
        string? site = "same-origin",
        string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/sign-in/local")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("name", name), new KeyValuePair<string, string>("password", password),
                new KeyValuePair<string, string>("return_url", returnUrl)
            ])
        };
        if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return client.SendAsync(request, Cancel);
    }

    private async Task<string> LocalSessionAsync(WebApplicationFactory<Program> hub)
    {
        using var client = Client(Here, hub);
        using var signIn = await SignInAsync(client, "ops", Password);
        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        return FakeProvider.SessionCookie(signIn);
    }

    private static async Task<HttpResponseMessage> WithCookieAsync(WebApplicationFactory<Program> hub, string cookie,
        string path)
    {
        using var client = FakeProvider.Client(hub);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-Forwarded-For", Here);
        return await client.SendAsync(request, Cancel);
    }

    // The page's one inline style, its faces included, is allowed by its hash, computed here as a browser does.
    private static void AssertPageHeaders(HttpResponseMessage response, string page)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
        var start = page.IndexOf("<style>", StringComparison.Ordinal) + "<style>".Length;
        var style = page[start..page.IndexOf("</style>", start, StringComparison.Ordinal)];
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style)));
        Assert.Equal(2, style.Split("@font-face{font-family:'DM Sans'").Length - 1);
        // The dark logo's rule must come after the one hiding it, or the dark theme shows no logo.
        Assert.True(style.IndexOf(".dark{display:none}", StringComparison.Ordinal) <
                    style.IndexOf("@media (prefers-color-scheme:dark)", StringComparison.Ordinal));
        Assert.Equal(
            $"default-src 'none'; font-src data:; style-src 'sha256-{hash}'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private sealed class Capture(ListLogger<Program> logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return logger;
        }

        public void Dispose()
        {
        }
    }

    // Every request reaches the Hub from the trusted proxy, as behind an ingress.
    private sealed class FromTheProxy : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Loopback;
                    return nextMiddleware(context);
                });
                next(app);
            };
        }
    }
}
