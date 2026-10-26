using System.Collections.Concurrent;

namespace IdempotencyGate.Stores;

/// <summary>
/// Single-process store. Correct for tests and single-instance deployments; on more than one
/// instance each replica keeps its own dictionary, which defeats the point — use a shared store.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IdempotencyRecord? Record;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Creates the store.</summary>
    public InMemoryIdempotencyStore(TimeProvider? timeProvider = null) =>
        _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IdempotencyBeginResult> BeginAsync(
        string key,
        string fingerprint,
        TimeSpan ttl,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var entry = _entries.GetOrAdd(key, static _ => new Entry());

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = entry.Record;

            var expired = existing is null || existing.ExpiresAt <= now;
            var leaseLost = existing is { State: IdempotencyState.InProgress }
                            && now - existing.CreatedAt > lease;

            if (expired || leaseLost)
            {
                entry.Record = new IdempotencyRecord(
                    key, fingerprint, IdempotencyState.InProgress, 0, null, null, now, now + ttl);
                return IdempotencyBeginResult.Started;
            }

            if (!string.Equals(existing!.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return IdempotencyBeginResult.KeyReuse(existing);
            }

            return existing.State == IdempotencyState.Completed
                ? IdempotencyBeginResult.Replay(existing)
                : IdempotencyBeginResult.InFlight;
        }
        finally
        {
            entry.Gate.Release();
        }
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
        var now = _time.GetUtcNow();
        var entry = _entries.GetOrAdd(key, static _ => new Entry());

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fingerprint = entry.Record?.Fingerprint ?? string.Empty;
            var created = entry.Record?.CreatedAt ?? now;
            entry.Record = new IdempotencyRecord(
                key, fingerprint, IdempotencyState.Completed, statusCode, contentType, body, created, now + ttl);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            // Do not remove the entry object: a concurrent request may be holding its gate.
            entry.Record = null;
        }

        return Task.CompletedTask;
    }
}
