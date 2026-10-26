using IdempotencyGate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IdempotencyGate.Tests;

internal sealed class Counter
{
    public int Charges;
    public bool Fail;
}

internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class TestApiOptions
{
    public Action<IdempotencyOptions>? Configure { get; set; }
    public int HandlerDelayMs { get; set; }
    public FakeTime? Time { get; set; }
}

internal sealed class TestApi : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApi(WebApplication app, Counter counter, HttpClient client, FakeTime? time)
    {
        _app = app;
        Counter = counter;
        Client = client;
        Time = time;
    }

    public Counter Counter { get; }

    public HttpClient Client { get; }

    public FakeTime? Time { get; }

    public static async Task<TestApi> StartAsync(TestApiOptions? options = null)
    {
        options ??= new TestApiOptions();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var time = options.Time ?? new FakeTime();
        var counter = new Counter();

        builder.Services.AddSingleton(counter);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddIdempotencyGate(options.Configure);

        var app = builder.Build();
        app.UseIdempotencyGate();

        app.MapGet("/health", () => Results.Text("ok"));

        app.MapPost("/charge", async (Counter state, HttpContext context) =>
        {
            Interlocked.Increment(ref state.Charges);

            if (options.HandlerDelayMs > 0)
            {
                await Task.Delay(options.HandlerDelayMs);
            }

            if (state.Fail)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync("boom");
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                chargeId = Guid.NewGuid().ToString("N"),
                count = state.Charges,
            });
        });

        await app.StartAsync();

        return new TestApi(app, counter, app.GetTestClient(), options.Time);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
