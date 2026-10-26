namespace IdempotencyGate;

/// <summary>Configuration for the idempotency gate.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Header the client sends. Stripe, Adyen, PayPal and most payment APIs use this exact name.</summary>
    public const string DefaultHeaderName = "Idempotency-Key";

    /// <summary>Name of the request header carrying the key.</summary>
    public string HeaderName { get; set; } = DefaultHeaderName;

    /// <summary>
    /// When true, a request on a protected method without a key is rejected with 400.
    /// Set to false for advisory mode during a migration.
    /// </summary>
    public bool RequireKey { get; set; } = true;

    /// <summary>HTTP methods that are gated. GET/HEAD must never be here.</summary>
    public HashSet<string> Methods { get; set; } =
        new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    /// <summary>How long a completed response stays replayable.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a request may hold the key before another request is allowed to take it over.
    /// This is the knob that turns a crashed request into a retry instead of a dead key.
    /// </summary>
    public TimeSpan InProgressLease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum accepted key length.</summary>
    public int MaxKeyLength { get; set; } = 255;

    /// <summary>Responses larger than this are executed but not cached, and the key is released.</summary>
    public int MaxCachedResponseBytes { get; set; } = 1024 * 1024;

    /// <summary>Requests larger than this are hashed in a streaming way instead of buffered.</summary>
    public int MaxRequestBytesForFingerprint { get; set; } = 1024 * 1024;

    /// <summary>Value sent in <c>Retry-After</c> while a duplicate request is in flight.</summary>
    public int RetryAfterSeconds { get; set; } = 1;
}
