using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IdempotencyGate.Stores;

namespace IdempotencyGate;

/// <summary>Registration helpers.</summary>
public static class IdempotencyGateExtensions
{
    /// <summary>
    /// Registers the idempotency gate. Registers <see cref="InMemoryIdempotencyStore"/> unless a
    /// different <see cref="IIdempotencyStore"/> is already in the container.
    /// </summary>
    public static IServiceCollection AddIdempotencyGate(
        this IServiceCollection services,
        Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<IdempotencyOptions>();

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }

    /// <summary>Adds the middleware. Place it before endpoints, after exception handling.</summary>
    public static IApplicationBuilder UseIdempotencyGate(this IApplicationBuilder app) =>
        app.UseMiddleware<IdempotencyMiddleware>();
}
