using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IdempotencyGate.EntityFrameworkCore;

/// <summary>Registration helpers for the EF Core store.</summary>
public static class IdempotencyGateEntityFrameworkExtensions
{
    /// <summary>
    /// Registers the EF Core store as <see cref="IIdempotencyStore"/>. Call it before
    /// <c>AddIdempotencyGate()</c> so the in-memory default is not used.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the provider, for example <c>o.UseSqlServer(...)</c>.</param>
    public static IServiceCollection AddIdempotencyGateEntityFrameworkStore(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.All(d => d.ServiceType != typeof(IDbContextFactory<IdempotencyGateDbContext>)))
        {
            services.AddDbContextFactory<IdempotencyGateDbContext>(configure);
        }

        services.TryAddSingleton<IIdempotencyStore, EfCoreIdempotencyStore>();
        return services;
    }
}
