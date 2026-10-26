using System.Net;
using System.Net.Http.Json;
using System.Text;
using IdempotencyGate;
using Xunit;

namespace IdempotencyGate.Tests;

public sealed class IdempotencyMiddlewareTests
{
    private static StringContent Json(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Post(string key, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/charge")
        {
            Content = Json(body),
        };
        request.Headers.Add(IdempotencyOptions.DefaultHeaderName, key);
        return request;
    }

    [Fact]
    public async Task Duplicate_request_replays_the_stored_response()
    {
        await using var api = await TestApi.StartAsync();

        var first = await api.Client.SendAsync(Post("key-1", """{"amount":10}"""));
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await api.Client.SendAsync(Post("key-1", """{"amount":10}"""));
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.False(first.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
        Assert.True(second.Headers.TryGetValues(IdempotencyMiddleware.ReplayedHeader, out var replayed));
        Assert.Equal("true", Assert.Single(replayed!));
        Assert.Equal(1, api.Counter.Charges);
    }

    [Fact]
    public async Task Concurrent_duplicates_execute_the_handler_exactly_once()
    {
        await using var api = await TestApi.StartAsync(new TestApiOptions { HandlerDelayMs = 400 });

        var requests = Enumerable.Range(0, 8)
            .Select(_ => api.Client.SendAsync(Post("key-race", """{"amount":10}""")))
            .ToArray();

        var responses = await Task.WhenAll(requests);

        Assert.Equal(1, api.Counter.Charges);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK
                                      && !r.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
        Assert.All(
            responses.Where(r => r.StatusCode == HttpStatusCode.Conflict),
            r => Assert.True(r.Headers.Contains("Retry-After")));
    }

    [Fact]
    public async Task Same_key_with_a_different_payload_is_rejected_as_reuse()
    {
        await using var api = await TestApi.StartAsync();

        await api.Client.SendAsync(Post("key-reuse", """{"amount":10}"""));
        var second = await api.Client.SendAsync(Post("key-reuse", """{"amount":999}"""));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal(1, api.Counter.Charges);
    }

    [Fact]
    public async Task Missing_key_is_rejected_on_a_gated_method()
    {
        await using var api = await TestApi.StartAsync();

        var response = await api.Client.PostAsync("/charge", Json("""{"amount":10}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, api.Counter.Charges);
    }

    [Fact]
    public async Task Advisory_mode_processes_requests_without_a_key()
    {
        await using var api = await TestApi.StartAsync(new TestApiOptions
        {
            Configure = o => o.RequireKey = false,
        });

        var response = await api.Client.PostAsync("/charge", Json("""{"amount":10}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, api.Counter.Charges);
    }

    [Fact]
    public async Task Server_error_releases_the_key_so_the_client_can_retry()
    {
        await using var api = await TestApi.StartAsync();
        api.Counter.Fail = true;

        var failed = await api.Client.SendAsync(Post("key-retry", """{"amount":10}"""));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        api.Counter.Fail = false;
        var retried = await api.Client.SendAsync(Post("key-retry", """{"amount":10}"""));

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, api.Counter.Charges);
    }

    [Fact]
    public async Task Client_error_is_stored_and_replayed()
    {
        await using var api = await TestApi.StartAsync();

        var first = await api.Client.SendAsync(Post("key-400", """{"amount":10}"""));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await api.Client.SendAsync(Post("key-400", """{"amount":10}"""));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, api.Counter.Charges);
    }

    [Fact]
    public async Task Key_expires_after_the_configured_ttl()
    {
        var time = new FakeTime();
        await using var api = await TestApi.StartAsync(new TestApiOptions
        {
            Time = time,
            Configure = o => o.Ttl = TimeSpan.FromMinutes(10),
        });

        await api.Client.SendAsync(Post("key-ttl", """{"amount":10}"""));
        Assert.Equal(1, api.Counter.Charges);

        time.Advance(TimeSpan.FromMinutes(11));

        var afterExpiry = await api.Client.SendAsync(Post("key-ttl", """{"amount":10}"""));

        Assert.Equal(HttpStatusCode.OK, afterExpiry.StatusCode);
        Assert.Equal(2, api.Counter.Charges);
    }

    [Fact]
    public async Task Get_requests_are_not_gated()
    {
        await using var api = await TestApi.StartAsync();

        var first = await api.Client.GetAsync("/health");
        var second = await api.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.False(second.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
    }

    [Fact]
    public async Task Distinct_keys_run_independently()
    {
        await using var api = await TestApi.StartAsync();

        await api.Client.SendAsync(Post("key-a", """{"amount":10}"""));
        await api.Client.SendAsync(Post("key-b", """{"amount":10}"""));

        Assert.Equal(2, api.Counter.Charges);
    }
}
