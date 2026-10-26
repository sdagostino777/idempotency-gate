using System.Net;
using System.Text;
using IdempotencyGate.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IdempotencyGate.Tests;

public sealed class EfCoreStoreTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A shared-cache in-memory SQLite database plus a store over it. The keeper connection holds
    /// the database in memory for the lifetime of the test; every context opens its own connection,
    /// which is what makes the concurrency test meaningful.
    /// </summary>
    private sealed class SqliteStore : IAsyncDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly ServiceProvider _provider;

        private SqliteStore(SqliteConnection keeper, ServiceProvider provider, EfCoreIdempotencyStore store)
        {
            _keeper = keeper;
            _provider = provider;
            Store = store;
        }

        public EfCoreIdempotencyStore Store { get; }

        public static async Task<SqliteStore> StartAsync(TimeProvider time)
        {
            var connectionString = $"Data Source=file:idem-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=30";

            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync();

            var services = new ServiceCollection();
            services.AddSingleton(time);
            services.AddDbContextFactory<IdempotencyGateDbContext>(o => o.UseSqlite(connectionString));

            var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<IdempotencyGateDbContext>>();

            await using (var context = await factory.CreateDbContextAsync())
            {
                await context.Database.EnsureCreatedAsync();
            }

            return new SqliteStore(keeper, provider, new EfCoreIdempotencyStore(factory, time));
        }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            return _keeper.DisposeAsync();
        }
    }

    [Fact]
    public async Task Completed_key_is_replayed_with_the_stored_response()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        var body = Encoding.UTF8.GetBytes("""{"chargeId":"abc"}""");

        var first = await db.Store.BeginAsync("k1", "fp-1", Ttl, Lease);
        Assert.Equal(IdempotencyBeginStatus.Started, first.Status);

        await db.Store.CompleteAsync("k1", 201, "application/json", body, Ttl);

        var second = await db.Store.BeginAsync("k1", "fp-1", Ttl, Lease);

        Assert.Equal(IdempotencyBeginStatus.Replay, second.Status);
        Assert.Equal(201, second.Record!.StatusCode);
        Assert.Equal("application/json", second.Record.ContentType);
        Assert.Equal(body, second.Record.Body);
    }

    [Fact]
    public async Task Key_reused_with_a_different_fingerprint_is_reported()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        await db.Store.BeginAsync("k2", "fp-1", Ttl, Lease);
        await db.Store.CompleteAsync("k2", 200, "application/json", [], Ttl);

        var reuse = await db.Store.BeginAsync("k2", "fp-2", Ttl, Lease);

        Assert.Equal(IdempotencyBeginStatus.KeyReuse, reuse.Status);
    }

    [Fact]
    public async Task Released_key_can_be_claimed_again()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        Assert.Equal(IdempotencyBeginStatus.Started, (await db.Store.BeginAsync("k3", "fp", Ttl, Lease)).Status);

        await db.Store.ReleaseAsync("k3");

        Assert.Equal(IdempotencyBeginStatus.Started, (await db.Store.BeginAsync("k3", "fp", Ttl, Lease)).Status);
    }

    [Fact]
    public async Task Expired_key_is_reclaimable()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        await db.Store.BeginAsync("k4", "fp", Ttl, Lease);
        await db.Store.CompleteAsync("k4", 200, "application/json", [], Ttl);

        Assert.Equal(IdempotencyBeginStatus.Replay, (await db.Store.BeginAsync("k4", "fp", Ttl, Lease)).Status);

        time.Advance(Ttl + TimeSpan.FromSeconds(1));

        Assert.Equal(IdempotencyBeginStatus.Started, (await db.Store.BeginAsync("k4", "fp", Ttl, Lease)).Status);
    }

    [Fact]
    public async Task Abandoned_in_progress_key_is_taken_over_after_the_lease()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        await db.Store.BeginAsync("k5", "fp", Ttl, Lease);

        Assert.Equal(IdempotencyBeginStatus.InProgress, (await db.Store.BeginAsync("k5", "fp", Ttl, Lease)).Status);

        time.Advance(Lease + TimeSpan.FromSeconds(1));

        Assert.Equal(IdempotencyBeginStatus.Started, (await db.Store.BeginAsync("k5", "fp", Ttl, Lease)).Status);
    }

    [Fact]
    public async Task Concurrent_callers_claim_the_key_exactly_once()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        var attempts = Enumerable.Range(0, 8)
            .Select(_ => db.Store.BeginAsync("race", "fp", Ttl, Lease))
            .ToArray();

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r.Status == IdempotencyBeginStatus.Started));
        Assert.All(
            results.Where(r => r.Status != IdempotencyBeginStatus.Started),
            r => Assert.Equal(IdempotencyBeginStatus.InProgress, r.Status));
    }

    [Fact]
    public async Task Purge_removes_only_expired_keys()
    {
        var time = new FakeTime();
        await using var db = await SqliteStore.StartAsync(time);

        await db.Store.BeginAsync("old", "fp", Ttl, Lease);
        await db.Store.CompleteAsync("old", 200, "application/json", [], Ttl);

        time.Advance(Ttl + TimeSpan.FromMinutes(1));
        await db.Store.BeginAsync("fresh", "fp", Ttl, Lease);

        var purged = await db.Store.PurgeExpiredAsync();

        Assert.Equal(1, purged);
    }

    [Fact]
    public async Task Middleware_over_the_database_store_executes_the_handler_once()
    {
        var connectionString = $"Data Source=file:e2e-{Guid.NewGuid():N}?mode=memory&cache=shared;Default Timeout=30";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();

        await using (var setup = new IdempotencyGateDbContext(
            new DbContextOptionsBuilder<IdempotencyGateDbContext>().UseSqlite(connectionString).Options))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        await using var api = await TestApi.StartAsync(new TestApiOptions
        {
            ConfigureServices = services =>
            {
                services.AddDbContextFactory<IdempotencyGateDbContext>(o => o.UseSqlite(connectionString));
                services.TryAddSingleton<IIdempotencyStore, EfCoreIdempotencyStore>();
            },
        });

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/charge")
        {
            Content = new StringContent("""{"amount":10}""", Encoding.UTF8, "application/json"),
        };
        request1.Headers.Add(IdempotencyOptions.DefaultHeaderName, "db-key");

        var response1 = await api.Client.SendAsync(request1);
        var body1 = await response1.Content.ReadAsStringAsync();

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/charge")
        {
            Content = new StringContent("""{"amount":10}""", Encoding.UTF8, "application/json"),
        };
        request2.Headers.Add(IdempotencyOptions.DefaultHeaderName, "db-key");

        var response2 = await api.Client.SendAsync(request2);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(body1, await response2.Content.ReadAsStringAsync());
        Assert.True(response2.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
        Assert.Equal(1, api.Counter.Charges);
    }
}
