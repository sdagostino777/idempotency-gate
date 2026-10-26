namespace IdempotencyGate;

/// <summary>
/// Storage for idempotency keys. Implementations must make <see cref="BeginAsync"/> atomic:
/// two concurrent calls with the same key may never both return <see cref="IdempotencyBeginStatus.Started"/>.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>Claims the key for the current request.</summary>
    Task<IdempotencyBeginResult> BeginAsync(
        string key,
        string fingerprint,
        TimeSpan ttl,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the response so later duplicates can be replayed.</summary>
    Task CompleteAsync(
        string key,
        int statusCode,
        string? contentType,
        byte[]? body,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the key so the client may retry (used when the request failed).</summary>
    Task ReleaseAsync(string key, CancellationToken cancellationToken = default);
}
