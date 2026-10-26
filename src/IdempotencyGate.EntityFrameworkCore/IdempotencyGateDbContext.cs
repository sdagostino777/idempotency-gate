using Microsoft.EntityFrameworkCore;

namespace IdempotencyGate.EntityFrameworkCore;

/// <summary>Stored state for one idempotency key.</summary>
public sealed class IdempotencyKeyEntity
{
    /// <summary>The client's key. Primary key of the table.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Hash of method, path, query and body: what the key was used for.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>In progress or completed.</summary>
    public IdempotencyState State { get; set; }

    /// <summary>HTTP status of the stored response.</summary>
    public int StatusCode { get; set; }

    /// <summary>Content type of the stored response.</summary>
    public string? ContentType { get; set; }

    /// <summary>Stored response body, up to the configured limit.</summary>
    public byte[]? Body { get; set; }

    /// <summary>When the key was claimed, in Unix milliseconds. Also the base of the in-progress lease.</summary>
    public long CreatedAtUnixMs { get; set; }

    /// <summary>After this instant the key is reclaimable, in Unix milliseconds.</summary>
    public long ExpiresAtUnixMs { get; set; }
}

/// <summary>
/// The table behind <see cref="EfCoreIdempotencyStore"/>. Timestamps are stored as Unix
/// milliseconds on purpose: <c>DateTimeOffset</c> comparisons do not translate on every provider
/// (SQLite in particular), and integers compare and index without surprises.
/// Add it to your application context or register it on its own — the store only needs
/// <see cref="IdempotencyKeyEntity"/>.
/// </summary>
public sealed class IdempotencyGateDbContext(DbContextOptions<IdempotencyGateDbContext> options)
    : DbContext(options)
{
    /// <summary>Idempotency keys.</summary>
    public DbSet<IdempotencyKeyEntity> Keys => Set<IdempotencyKeyEntity>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var entity = modelBuilder.Entity<IdempotencyKeyEntity>();
        entity.ToTable("IdempotencyKeys");
        entity.HasKey(x => x.Key);
        entity.Property(x => x.Key).HasMaxLength(255);
        entity.Property(x => x.Fingerprint).HasMaxLength(128).IsRequired();
        entity.Property(x => x.ContentType).HasMaxLength(256);
        entity.Property(x => x.State).HasConversion<int>();

        // Purge queries and the reclaim check both scan by expiry.
        entity.HasIndex(x => x.ExpiresAtUnixMs);
    }
}
