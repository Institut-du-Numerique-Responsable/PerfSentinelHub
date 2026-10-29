using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests.Api;

/// <summary>
///     Declaring who works on a finding. Access is off here, so every caller is an admin and the
///     identity is the proxy header; AccessRoutesTests owns what each role may do.
/// </summary>
public sealed class ActionsApiTests(HubApplicationFactory factory)
    : IClassFixture<HubApplicationFactory>, IAsyncLifetime
{
    // The factory's clock, fixed.
    private const long NowMs = 10_000;
    private HttpClient _client = null!;

    private WebApplicationFactory<Program> _scoped = null!;

    public ValueTask InitializeAsync()
    {
        _scoped = AckRelayApiTests.Scoped(factory, true, TimeSpan.FromSeconds(10));
        _client = _scoped.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _scoped.DisposeAsync();
    }

    [Fact]
    public async Task An_action_needs_an_identity()
    {
        var signature = await SeedAsync("no-identity");

        using var response = await SendAsync("POST", Body(signature, take: true), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("The Hub does not know who is asking, and an action is taken in somebody's name.",
            await DetailAsync(response));
    }

    [Fact]
    public async Task Taking_a_finding_sets_in_progress_and_the_caller_as_owner()
    {
        var signature = await SeedAsync("take");

        using var response = await SendAsync("POST", Body(signature, take: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal(signature, body["signature"]!.GetValue<string>());
        Assert.Equal("in_progress", body["status"]!.GetValue<string>());
        Assert.Equal("alice", body["owner"]!.GetValue<string>());
        Assert.Equal("alice", body["created_by"]!.GetValue<string>());
        var action = Assert.Single(await ReadAsync(signature));
        Assert.Equal(new StoredActionEvent(null, "in_progress", "alice", "alice", NowMs), Assert.Single(action.Events));
    }

    [Fact]
    public async Task A_create_with_a_status_and_an_owner_keeps_both()
    {
        var signature = await SeedAsync("status-owner");

        using var response = await SendAsync("POST", Body(signature, "todo", "bob"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("todo", body["status"]!.GetValue<string>());
        Assert.Equal("bob", body["owner"]!.GetValue<string>());
        Assert.Equal("alice", body["created_by"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_second_create_is_a_409()
    {
        var signature = await SeedAsync("twice");
        using var first = await SendAsync("POST", Body(signature, take: true));

        using var second = await SendAsync("POST", Body(signature, "done"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("This finding already has an action; change it with PATCH.", await DetailAsync(second));
    }

    [Fact]
    public async Task An_unknown_finding_is_a_404()
    {
        using var response = await SendAsync("POST", Body("no-such-finding", take: true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("The Hub holds no such finding.", await DetailAsync(response));
    }

    [Fact]
    public async Task Done_stamps_done_at_and_logs_the_transition()
    {
        var signature = await SeedAsync("done");
        using var created = await SendAsync("POST", Body(signature, take: true));

        using var response = await SendAsync("PATCH", Body(signature, "done"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("done", body["status"]!.GetValue<string>());
        Assert.Equal(NowMs, body["done_at_ms"]!.GetValue<long>());
        var action = Assert.Single(await ReadAsync(signature));
        Assert.Equal(2, action.Events.Count);
        Assert.Equal(new StoredActionEvent("in_progress", "done", "alice", "alice", NowMs), action.Events[1]);
    }

    [Fact]
    public async Task Leaving_done_clears_done_at()
    {
        var signature = await SeedAsync("reopen");
        using var created = await SendAsync("POST", Body(signature, "done"));

        using var response = await SendAsync("PATCH", Body(signature, "in_progress"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await JsonOf(response))["done_at_ms"]);
    }

    [Fact]
    public async Task A_patch_with_a_null_owner_clears_it()
    {
        var signature = await SeedAsync("clear-owner");
        using var created = await SendAsync("POST", Body(signature, take: true));

        using var response = await SendAsync("PATCH", $$"""{"signature":"{{signature}}","owner":null}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await JsonOf(response))["owner"]);
    }

    [Fact]
    public async Task A_patch_of_a_finding_without_an_action_is_a_404()
    {
        var signature = await SeedAsync("no-action");

        using var response = await SendAsync("PATCH", Body(signature, "done"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("This finding has no action yet.", await DetailAsync(response));
    }

    [Fact]
    public async Task A_patch_with_nothing_to_change_is_a_400()
    {
        var signature = await SeedAsync("nothing");

        using var response = await SendAsync("PATCH", $$"""{"signature":"{{signature}}"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_patch_cannot_take()
    {
        var signature = await SeedAsync("patch-take");
        using var created = await SendAsync("POST", Body(signature, "todo"));

        using var response = await SendAsync("PATCH", Body(signature, take: true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("take applies to a new action only.", await DetailAsync(response));
    }

    [Theory]
    [InlineData("""{"signature":"SIG"}""")]
    [InlineData("""{"signature":"SIG","owner":"bob"}""")]
    public async Task A_create_without_status_or_take_is_a_400(string body)
    {
        var signature = await SeedAsync("bare");

        using var response = await SendAsync("POST", body.Replace("SIG", signature, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("An action needs a status or take.", await DetailAsync(response));
    }

    [Theory]
    [InlineData("""{"signature":"SIG","take":true,"status":"todo"}""")]
    [InlineData("""{"signature":"SIG","take":true,"owner":"bob"}""")]
    public async Task Take_with_a_status_or_an_owner_is_a_400(string body)
    {
        var signature = await SeedAsync("take-plus");

        using var response = await SendAsync("POST", body.Replace("SIG", signature, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_257_char_owner_is_a_400()
    {
        var signature = await SeedAsync("long-owner");

        using var response = await SendAsync("POST", Body(signature, "todo", new string('o', 257)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"status":"todo"}""")]
    [InlineData("""{"signature":".","status":"todo"}""")]
    [InlineData("""{"signature":"..","status":"todo"}""")]
    [InlineData("""{"signature":"a\u0007b","status":"todo"}""")]
    [InlineData("""{"signature":"s","status":"finished"}""")]
    [InlineData("""{"signature":"s","status":7}""")]
    [InlineData("""{"signature":"s","status":"todo","owner":""}""")]
    [InlineData("""{"signature":"s","status":"todo","owner":"a\nb"}""")]
    [InlineData("""{"signature":"s","status":"todo","owner":7}""")]
    [InlineData("""{"signature":"s","take":"yes"}""")]
    // Not a string .NET can hold, which the reader throws on.
    [InlineData("""{"signature":"\ud800","status":"todo"}""")]
    public async Task An_invalid_body_is_a_400(string body)
    {
        using var response = await SendAsync("POST", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_body_over_the_cap_is_a_413()
    {
        using var response = await SendAsync("POST", Body(new string('s', 9 * 1024), "todo"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_site_write_is_refused()
    {
        var signature = await SeedAsync("cross-site");

        using var response = await SendAsync("POST", Body(signature, take: true), "alice", "cross-site");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await ReadAsync(signature));
    }

    [Fact]
    public async Task A_form_body_is_a_415()
    {
        // The identity comes first in the route order, so this caller carries one.
        var signature = await SeedAsync("form");
        using var request = Request("POST", "alice", null);
        request.Content =
            new StringContent(Body(signature, take: true), Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    private async Task<string> SeedAsync(string name)
    {
        var signature = $"n_plus_one_sql:order-svc:{name}";
        await factory.Database.UpsertBatchAsync(
            new SourceSnapshot("test", "Test", "test", "0.26.2"),
            FindingJson.Batch(FindingJson.Envelope(signature)),
            NowMs,
            TestContext.Current.CancellationToken);
        return signature;
    }

    private Task<HttpResponseMessage> SendAsync(string method, string body, string? identity = "alice",
        string? site = null)
    {
        var request = Request(method, identity, site);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static HttpRequestMessage Request(string method, string? identity, string? site)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), "/api/actions");
        if (identity is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-User", identity);
        if (site is not null)
            request.Headers.Add("Sec-Fetch-Site", site);
        return request;
    }

    private static string Body(string signature, string? status = null, string? owner = null, bool take = false)
    {
        var body = new JsonObject { ["signature"] = signature };
        if (status is not null) body["status"] = status;
        if (owner is not null) body["owner"] = owner;
        if (take) body["take"] = true;
        return body.ToJsonString();
    }

    private static async Task<JsonObject> JsonOf(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!
            .AsObject();
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response)
    {
        return (await JsonOf(response))["detail"]!.GetValue<string>();
    }

    private Task<IReadOnlyList<StoredAction>> ReadAsync(string signature)
    {
        return factory.Database.ReadActionsAsync([signature], TestContext.Current.CancellationToken);
    }
}
