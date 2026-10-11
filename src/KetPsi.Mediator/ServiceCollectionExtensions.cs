using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KetPsi.Mediator;

/// <summary>
/// Manual DI registration helpers.
/// </summary>
/// <remarks>
/// Prefer the generated <c>AddMediatorHandlers()</c> extension, which registers
/// <see cref="IMediator"/>, all discovered handlers, and all discovered behaviors
/// in a single call. This class remains available for advanced scenarios.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default <see cref="IMediator"/> implementation.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <remarks>
    /// Handlers and behaviors must still be registered separately
    /// (or via the generated <c>AddMediatorHandlers</c> method).
    /// </remarks>
    public static IServiceCollection AddKetPsiMediator(this IServiceCollection services)
    {
        services.TryAddSingleton<IMediator, Mediator>();
        return services;
    }
}
