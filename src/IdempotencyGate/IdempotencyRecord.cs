namespace IdempotencyGate;

/// <summary>Lifecycle of a key: claimed by one request, then completed or released.</summary>
public enum IdempotencyState
{
    /// <summary>The first request holds the key and is still running.</summary>
    InProgress,

    /// <summary>A response is stored and will be replayed for duplicates.</summary>
    Completed,
}

/// <summary>What happened when a request tried to claim a key.</summary>
public enum IdempotencyBeginStatus
{
    /// <summary>The caller owns the key and must execute the request.</summary>
    Started,

    /// <summary>This key already produced a response: replay it.</summary>
    Replay,

    /// <summary>Another request is executing with this key right now.</summary>
    InProgress,

    /// <summary>The key was reused with a different request. That is a client bug, not a retry.</summary>
    KeyReuse,
}

/// <summary>Stored state for one idempotency key.</summary>
public sealed record IdempotencyRecord(
    string Key,
    string Fingerprint,
    IdempotencyState State,
    int StatusCode,
    string? ContentType,
    byte[]? Body,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Result of trying to claim a key.</summary>
public sealed record IdempotencyBeginResult(IdempotencyBeginStatus Status, IdempotencyRecord? Record = null)
{
    /// <summary>The caller owns the key.</summary>
    public static readonly IdempotencyBeginResult Started = new(IdempotencyBeginStatus.Started);

    /// <summary>Another request holds the key.</summary>
    public static readonly IdempotencyBeginResult InFlight = new(IdempotencyBeginStatus.InProgress);

    /// <summary>Replay the stored response.</summary>
    public static IdempotencyBeginResult Replay(IdempotencyRecord record) =>
        new(IdempotencyBeginStatus.Replay, record);

    /// <summary>The key was reused for a different request.</summary>
    public static IdempotencyBeginResult KeyReuse(IdempotencyRecord record) =>
        new(IdempotencyBeginStatus.KeyReuse, record);
}
