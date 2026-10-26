using Microsoft.EntityFrameworkCore;

namespace IdempotencyGate.EntityFrameworkCore;

/// <summary>
/// Database-backed store: keys are shared by every instance of the API, which is the only
/// configuration in which idempotency survives a load balancer.
/// </summary>
/// <remarks>
/// The claim path is built on two atomic operations, never on a read followed by a write:
/// a conditional <c>UPDATE ... WHERE expired OR lease lost</c> and an <c>INSERT</c> that fails
/// on the primary key. Both let the database decide the winner, so two instances racing for the
/// same key cannot both start executing.
/// </remarks>
public sealed class EfCoreIdempotencyStore : IIdempotencyStore
{
    private readonly IDbContextFactory<IdempotencyGateDbContext> _factory;
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    public EfCoreIdempotencyStore(
        IDbContextFactory<IdempotencyGateDbContext> contextFactory,
        TimeProvider? timeProvider = null)
    {
        _factory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<IdempotencyBeginResult> BeginAsync(
        string key,
        string fingerprint,
        TimeSpan ttl,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var staleBeforeMs = nowMs - (long)lease.TotalMilliseconds;
        var expiresAtMs = nowMs + (long)ttl.TotalMilliseconds;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // 1. Reclaim a key that expired or whose request died: one statement, atomic in the database.
        var reclaimed = await db.Keys
            .Where(k => k.Key == key
                        && (k.ExpiresAtUnixMs <= nowMs
                            || (k.State == IdempotencyState.InProgress && k.CreatedAtUnixMs <= staleBeforeMs)))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(k => k.Fingerprint, fingerprint)
                    .SetProperty(k => k.State, IdempotencyState.InProgress)
                    .SetProperty(k => k.StatusCode, 0)
                    .SetProperty(k => k.ContentType, (string?)null)
                    .SetProperty(k => k.Body, (byte[]?)null)
                    .SetProperty(k => k.CreatedAtUnixMs, nowMs)
                    .SetProperty(k => k.ExpiresAtUnixMs, expiresAtMs),
                cancellationToken)
            .ConfigureAwait(false);

        if (reclaimed == 1)
        {
            return IdempotencyBeginResult.Started;
        }

        // 2. No such key: insert it. The primary key decides the winner of a race.
        var entity = new IdempotencyKeyEntity
        {
            Key = key,
            Fingerprint = fingerprint,
            State = IdempotencyState.InProgress,
            StatusCode = 0,
            CreatedAtUnixMs = nowMs,
            ExpiresAtUnixMs = expiresAtMs,
        };

        db.Keys.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return IdempotencyBeginResult.Started;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(entity).State = EntityState.Detached;
        }

        // 3. Someone else owns the key: report what it holds.
        var existing = await db.Keys
            .AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // Deleted between the insert and the read. Let the caller retry rather than guess.
            return IdempotencyBeginResult.InFlight;
        }

        if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return IdempotencyBeginResult.KeyReuse(ToRecord(existing));
        }

        return existing.State == IdempotencyState.Completed
            ? IdempotencyBeginResult.Replay(ToRecord(existing))
            : IdempotencyBeginResult.InFlight;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(
        string key,
        int statusCode,
        string? contentType,
        byte[]? body,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var expiresAtMs = _time.GetUtcNow().ToUnixTimeMilliseconds() + (long)ttl.TotalMilliseconds;

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Keys
            .Where(k => k.Key == key)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(k => k.State, IdempotencyState.Completed)
                    .SetProperty(k => k.StatusCode, statusCode)
                    .SetProperty(k => k.ContentType, contentType)
                    .SetProperty(k => k.Body, body)
                    .SetProperty(k => k.ExpiresAtUnixMs, expiresAtMs),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await db.Keys
            .Where(k => k.Key == key)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Deletes keys that expired before <paramref name="now"/>. Call it from a scheduled job.</summary>
    public async Task<int> PurgeExpiredAsync(DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    {
        var cutoffMs = (now ?? _time.GetUtcNow()).ToUnixTimeMilliseconds();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await db.Keys
            .Where(k => k.ExpiresAtUnixMs <= cutoffMs)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static IdempotencyRecord ToRecord(IdempotencyKeyEntity entity) =>
        new(
            entity.Key,
            entity.Fingerprint,
            entity.State,
            entity.StatusCode,
            entity.ContentType,
            entity.Body,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMs),
            DateTimeOffset.FromUnixTimeMilliseconds(entity.ExpiresAtUnixMs));

    /// <summary>
    /// Recognises a primary-key violation across the usual providers. Matching on the message is
    /// ugly, and it is what provider-agnostic code can do without taking a dependency per provider.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;

            if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)   // SQLite
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)           // SQL Server
                || message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase) // PostgreSQL
                || message.Contains("23505", StringComparison.Ordinal))                            // PostgreSQL SQLSTATE
            {
                return true;
            }
        }

        return false;
    }
}
