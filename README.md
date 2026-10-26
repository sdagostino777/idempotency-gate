# IdempotencyGate

[![ci](https://github.com/sdagostino777/idempotency-gate/actions/workflows/ci.yml/badge.svg)](https://github.com/sdagostino777/idempotency-gate/actions/workflows/ci.yml)

**Idempotency-Key middleware for ASP.NET Core.** A retried request replays the first
response instead of executing the handler twice — so a client that times out and retries
does not get charged twice, shipped twice, or emailed twice.

Two packages:

| Package | Store | Use it when |
| --- | --- | --- |
| `IdempotencyGate` | in-process memory | single instance, tests, prototypes |
| `IdempotencyGate.EntityFrameworkCore` | SQL Server, PostgreSQL, SQLite, MySQL | anything behind a load balancer |

---

## The failure this prevents

A payment endpoint takes 1.5 seconds. The client gives up at 1.2, sees a timeout, and
retries. The retry hits the handler again. Two charges, one order, and a support ticket
that says *"your app charged me twice"*.

The fix is not a `distinct` clause. It is a contract: the client sends
`Idempotency-Key: <uuid>` with the request, and the server guarantees that the same key
produces the same response no matter how many times it arrives. This middleware
implements that contract.

It is the same mechanism Stripe, Adyen and PayPal expose, and the one most in-house
APIs forget until after the first double charge.

## Behaviour

| Situation | Response |
| --- | --- |
| First request with a key | Handler executes, response stored, header `Idempotency-Replayed` absent |
| Duplicate after completion, same payload | Stored response replayed byte for byte, `Idempotency-Replayed: true` |
| Duplicate while the first is still running | `409 Conflict` + `Retry-After` — never executed in parallel |
| Same key, different payload | `422 Unprocessable Entity` — a key belongs to one request, this is a client bug |
| Handler returned 5xx | Key released, so the client's retry is allowed to run |
| Key not sent on a gated method | `400 Bad Request` (configurable to advisory mode) |
| Key older than `Ttl` | Treated as new, handler runs again |

## Quick start

```csharp
builder.Services.AddIdempotencyGate(options =>
{
    options.Ttl = TimeSpan.FromHours(24);
    options.RequireKey = true;          // false = advisory mode during migration
});

var app = builder.Build();

app.UseIdempotencyGate();               // after exception handling, before endpoints
```

```bash
curl -X POST http://localhost:5000/payments \
  -H 'Idempotency-Key: 6f1a9d2e-3c4b-4f5a-9e8d-1a2b3c4d5e6f' \
  -H 'Content-Type: application/json' \
  -d '{"amount": 42, "currency": "EUR"}'
```

Send it twice: the second response is identical, carries `Idempotency-Replayed: true`,
and the handler never runs again. `samples/IdempotencyGate.Sample` is a runnable demo
with a deliberately slow endpoint.

## Configuration

| Option | Default | Why you would change it |
| --- | --- | --- |
| `HeaderName` | `Idempotency-Key` | Your API already uses another header |
| `RequireKey` | `true` | `false` to introduce the gate without breaking existing clients |
| `Methods` | POST, PUT, PATCH, DELETE | Narrow it to the endpoints that move money |
| `Ttl` | 24 h | How long a client may retry. Never longer than your request retention policy |
| `InProgressLease` | 60 s | How long a crashed request may hold a key before another request takes it over |
| `MaxKeyLength` | 255 | — |
| `MaxCachedResponseBytes` | 1 MiB | Larger responses run but are not cached, and the key is released |
| `RetryAfterSeconds` | 1 | Advertised to the client while a duplicate is in flight |

## Production notes

These are the parts that decide whether this works in production, not in a demo.

- **One store per deployment, shared by every instance.** `InMemoryIdempotencyStore` is
  correct for a single process; behind a load balancer it is worse than nothing. Use
  `IdempotencyGate.EntityFrameworkCore` (SQL Server, PostgreSQL, SQLite, MySQL) or
  implement `IIdempotencyStore` over Redis, and register it before `AddIdempotencyGate`.
- **`BeginAsync` must be atomic.** The interface exists to make that requirement explicit:
  two concurrent calls with the same key may never both return `Started`. With a SQL
  store, that is a unique index and a caught duplicate-key error, not a `SELECT` followed
  by an `INSERT`.
- **Fail open, log loud.** If the store throws, the request executes unprotected and the
  failure is logged as an error. Refusing every write because the cache is down is a
  worse outage than the one you are preventing.
- **State updates ignore the request's cancellation token.** If a client disconnects
  mid-flight, the key is still completed or released. Otherwise it stays claimed until
  the lease expires and the client's retry gets a 409 it cannot act on.
- **Do not gate GET.** It is read-only, and caching reads here duplicates HTTP caching
  badly. `Methods` deliberately starts without it.

## Production with several instances: the EF Core store

`InMemoryIdempotencyStore` keeps keys inside one process. Two replicas behind a load
balancer means a retry can land on the replica that never saw the first request, and the
protection is gone. The EF Core store puts the keys in the database instead:

```csharp
builder.Services.AddIdempotencyGateEntityFrameworkStore(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddIdempotencyGate();   // store first, then the gate
```

Then create the table. Migrations if the project already has them, otherwise:

```csharp
await using var scope = app.Services.CreateAsyncScope();
await scope.ServiceProvider
    .GetRequiredService<IDbContextFactory<IdempotencyGateDbContext>>()
    .CreateDbContext()
    .Database.EnsureCreatedAsync();
```

What happens in the database on a race:

| Step | Statement | Why it is safe |
| --- | --- | --- |
| reclaim | `UPDATE ... WHERE key = @k AND (expired OR lease lost)` | the database returns one winner; the losers move on |
| claim | `INSERT` with the key as primary key | a duplicate key is the database saying "someone else won" |
| decide | `SELECT` and compare fingerprints | read-only, after the winner is already decided |

There is never a read followed by a write, which is the mistake that makes most
hand-rolled idempotency implementations fail under load.

Also worth scheduling:

```csharp
await store.PurgeExpiredAsync();   // daily: deletes keys past their TTL
```

## Extending

```csharp
public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis) : IIdempotencyStore
{
    // BeginAsync: SET NX with the fingerprint; on conflict, read and decide replay /
    // in-flight / key-reuse from the stored state.
    // CompleteAsync: write the response with the TTL.
    // ReleaseAsync: delete the key.
}

builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
builder.Services.AddIdempotencyGate();
```

## Tests

```bash
dotnet test
```

Eighteen tests cover the whole contract: the middleware behaviours above, the store
semantics on a real SQLite database, and the two that matter most — eight concurrent
duplicates cause exactly one execution, and a 5xx releases the key so the retry runs.
The end-to-end test runs the middleware over the EF Core store, so the integration is
covered and not assumed.

## Licence

MIT.
