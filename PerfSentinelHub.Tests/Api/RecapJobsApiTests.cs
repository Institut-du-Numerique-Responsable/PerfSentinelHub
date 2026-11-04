using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using PerfSentinelHub.Recaps;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     The recap job routes on the factory's clock, access off. What each caller reads of a job is
///     AccessRoutesTests' business, and how the registry keeps and reuses jobs RecapJobsTests'.
/// </summary>
public sealed class RecapJobsApiTests(HubApplicationFactory factory) : IClassFixture<HubApplicationFactory>, IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _hubs = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var hub in _hubs)
            hub.Dispose();
    }

    [Fact]
    public async Task A_job_runs_to_the_recap_GET_api_recap_answers_in_each_view()
    {
        using var client = factory.CreateClient();

        using var started = await client.SendAsync(Start("period=day&refresh=true"), Token);

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var id = await IdAsync(started);
        Assert.Equal($"/api/recap/jobs/{id}", started.Headers.Location?.OriginalString);
        var done = await SettledAsync(client, id);
        Assert.Equal("succeeded", done.GetProperty("status").GetString());
        Assert.Equal(1, done.GetProperty("progress").GetDouble());
        Assert.True(done.GetProperty("expires_at_ms").GetInt64() > done.GetProperty("finished_at_ms").GetInt64());
        Assert.All(done.GetProperty("stages").EnumerateArray(), stage =>
            Assert.Contains(stage.GetProperty("state").GetString(), new[] { "done", "skipped" }));

        foreach (var query in new[] { "", "?view=pilotage", "?moves=all", "?view=pilotage&moves=all" })
        {
            var direct = await client.GetStringAsync(
                "/api/recap?period=day" + (query.Length == 0 ? "" : "&" + query[1..]), Token);
            var fromJob = await client.GetStringAsync($"/api/recap/jobs/{id}/result{query}", Token);
            Assert.Equal(direct, fromJob);
        }
    }

    [Fact]
    public async Task A_second_start_reuses_the_job_and_a_refresh_computes_again()
    {
        using var client = factory.CreateClient();
        using var first = await client.SendAsync(Start("period=month&refresh=true"), Token);
        var id = await IdAsync(first);
        await SettledAsync(client, id);

        using var again = await client.SendAsync(Start("period=month"), Token);
        using var refreshed = await client.SendAsync(Start("period=month&refresh=true"), Token);

        Assert.Equal(id, await IdAsync(again));
        Assert.NotEqual(id, await IdAsync(refreshed));
    }

    // Every day of one period computes the same recap, so a day named inside it reuses the job of another.
    [Fact]
    public async Task Two_days_of_one_period_share_a_job()
    {
        using var client = factory.CreateClient();
        using var first = await client.SendAsync(Start("period=week&at=1969-12-30&refresh=true"), Token);
        var id = await IdAsync(first);
        await SettledAsync(client, id);

        using var again = await client.SendAsync(Start("period=week&at=1969-12-31"), Token);

        Assert.Equal(id, await IdAsync(again));
    }

    [Fact]
    public async Task A_job_is_started_from_this_site_with_a_JSON_request_only()
    {
        using var client = factory.CreateClient();
        using var foreign = Start("period=day");
        foreign.Headers.Remove("Sec-Fetch-Site");
        foreign.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var plain = new HttpRequestMessage(HttpMethod.Post, "/api/recap/jobs?period=day");

        using var refusedForeign = await client.SendAsync(foreign, Token);
        using var refusedPlain = await client.SendAsync(plain, Token);

        Assert.Equal(HttpStatusCode.Forbidden, refusedForeign.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, refusedPlain.StatusCode);
    }

    [Theory]
    [InlineData("period=year")]
    [InlineData("at=1970-01-02")]
    [InlineData("environment=nowhere")]
    [InlineData("refresh=yes")]
    [InlineData("period=day&period=week")]
    public async Task A_bad_query_is_a_400(string query)
    {
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Start(query), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_job_is_a_404_on_every_route()
    {
        using var client = factory.CreateClient();
        const string id = "0123456789abcdef0123456789abcdef";

        using var read = await client.GetAsync($"/api/recap/jobs/{id}", Token);
        using var result = await client.GetAsync($"/api/recap/jobs/{id}/result", Token);
        using var cancelled = await client.SendAsync(Cancel(id), Token);

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, cancelled.StatusCode);
    }

    [Fact]
    public async Task A_running_job_has_no_result_yet_and_its_cancellation_forgets_it()
    {
        using var client = Stalled();
        using var started = await client.SendAsync(Start("period=day"), Token);
        var id = await IdAsync(started);

        using var early = await client.GetAsync($"/api/recap/jobs/{id}/result", Token);
        using var badView = await client.GetAsync($"/api/recap/jobs/{id}/result?view=other", Token);
        using var cancelled = await client.SendAsync(Cancel(id), Token);
        using var gone = await client.GetAsync($"/api/recap/jobs/{id}", Token);

        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("recap_not_ready", await CodeAsync(early));
        Assert.Equal(HttpStatusCode.BadRequest, badView.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task The_result_of_a_failed_job_says_it_will_never_come()
    {
        using var client = Failing();
        using var started = await client.SendAsync(Start("period=day"), Token);
        var id = await IdAsync(started);
        var failed = await SettledAsync(client, id);

        using var result = await client.GetAsync($"/api/recap/jobs/{id}/result", Token);

        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("recap_failed", failed.GetProperty("error_code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
        Assert.Equal("recap_failed", await CodeAsync(result));
    }

    [Fact]
    public async Task Each_screen_named_in_X_Recap_Screen_lets_go_of_its_own_follow()
    {
        using var client = Stalled();
        using var first = Start("period=day&service=screens");
        first.Headers.Add("X-Recap-Screen", "screen-1");
        using var second = Start("period=day&service=screens");
        second.Headers.Add("X-Recap-Screen", "screen-2");
        using var started = await client.SendAsync(first, Token);
        var id = await IdAsync(started);
        using var joined = await client.SendAsync(second, Token);
        Assert.Equal(id, await IdAsync(joined));

        using var letGoFirst = Cancel(id);
        letGoFirst.Headers.Add("X-Recap-Screen", "screen-1");
        using var afterFirst = await client.SendAsync(letGoFirst, Token);
        using var stillRunning = await client.GetAsync($"/api/recap/jobs/{id}", Token);
        using var letGoSecond = Cancel(id);
        letGoSecond.Headers.Add("X-Recap-Screen", "screen-2");
        using var afterSecond = await client.SendAsync(letGoSecond, Token);
        using var gone = await client.GetAsync($"/api/recap/jobs/{id}", Token);

        Assert.Equal(HttpStatusCode.NoContent, afterFirst.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillRunning.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, afterSecond.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Past_the_running_cap_a_start_is_a_503_with_retry_after()
    {
        using var client = Stalled();
        for (var index = 0; index < RecapJobs.MaxRunning; index++)
        {
            using var running = await client.SendAsync(Start($"period=day&service=s{index}"), Token);
            Assert.Equal(HttpStatusCode.Accepted, running.StatusCode);
        }

        using var refused = await client.SendAsync(Start("period=day&service=another"), Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal("5", refused.Headers.GetValues("Retry-After").Single());
        Assert.Equal("recap_busy", await CodeAsync(refused));
    }

    [Fact]
    public async Task The_metrics_count_recap_jobs()
    {
        using var client = factory.CreateClient();
        using var started = await client.SendAsync(Start("period=week&refresh=true"), Token);
        await SettledAsync(client, await IdAsync(started));

        var metrics = await client.GetStringAsync("/metrics", Token);

        Assert.Contains("perf_sentinel_hub_recap_jobs{state=\"kept\"}", metrics, StringComparison.Ordinal);
        Assert.Contains("perf_sentinel_hub_recap_jobs_total{outcome=\"busy\"} ", metrics, StringComparison.Ordinal);
        Assert.Matches("perf_sentinel_hub_recap_jobs_total\\{outcome=\"succeeded\"\\} [1-9]", metrics);
    }

    // ------------------------------------------------------------ helpers

    internal static HttpRequestMessage Start(string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/recap/jobs?" + query)
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    internal static HttpRequestMessage Cancel(string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/recap/jobs/{id}")
        {
            Content = new StringContent("", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return request;
    }

    internal static async Task<string> IdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> SettledAsync(HttpClient client, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync($"/api/recap/jobs/{id}", Token));
            if (document.RootElement.GetProperty("status").GetString() != "running")
                return document.RootElement.Clone();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The recap job never finished.");
            await Task.Delay(20, Token);
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.GetProperty("code").GetString();
    }

    // A Hub whose recaps fail at once.
    private HttpClient Failing()
    {
        return With((_, _, _) => Task.FromException<RecapSnapshot>(new InvalidOperationException("disk")));
    }

    // A Hub whose recaps never end until cancelled, so a job stays running for as long as a test needs.
    private HttpClient Stalled()
    {
        return With((_, _, token) =>
        {
            var never = new TaskCompletionSource<RecapSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => never.TrySetCanceled(token));
            return never.Task;
        });
    }

    private HttpClient With(Func<RecapRequest, IRecapProgress, CancellationToken, Task<RecapSnapshot>> compute)
    {
        var hub = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<RecapJobs>();
            services.AddSingleton(new RecapJobs(
                compute,
                () => 0,
                TimeProvider.System,
                CancellationToken.None,
                NullLogger<RecapJobs>.Instance));
        }));
        _hubs.Add(hub);
        return hub.CreateClient();
    }
}
