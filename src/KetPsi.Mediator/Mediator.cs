using Microsoft.Extensions.DependencyInjection;

namespace KetPsi.Mediator;

/// <summary>
/// Default runtime implementation of <see cref="IMediator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Prefer the source-generated interceptor path for maximum performance.
/// This class is used as a fallback when the request type is not known statically
/// (for example when the call site receives an <see cref="ICommand"/> variable).
/// </para>
/// <para>
/// The reflection-based resolution path is intentionally simple and correct;
/// it is not optimised for the hot path.
/// </para>
/// </remarks>
public sealed partial class Mediator : IMediator
{
    /// <summary>
    /// The service provider used to resolve handlers and pipeline behaviors.
    /// Exposed for source-generated interceptors in the same compilation.
    /// </summary>
    public readonly IServiceProvider Services;

    /// <summary>
    /// Creates a new <see cref="Mediator"/> that resolves handlers from
    /// <paramref name="services"/>.
    /// </summary>
    /// <param name="services">
    /// The application service provider. Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public Mediator(IServiceProvider services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public ValueTask Send(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var handlerType = typeof(ICommandHandler<>).MakeGenericType(command.GetType());
        var handler = Services.GetRequiredService(handlerType);

        // Reflection fallback – only used when the interceptor did not rewrite the call site.
        var method = handlerType.GetMethod(nameof(ICommandHandler<ICommand>.Handle))!;
        return (ValueTask)method.Invoke(handler, new object[] { command, cancellationToken })!;
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        // Prefer a command handler with response when the request is a command.
        if (request is ICommand)
        {
            var commandHandlerType = typeof(ICommandHandler<,>).MakeGenericType(requestType, typeof(TResponse));
            var commandHandler = Services.GetService(commandHandlerType);
            if (commandHandler is not null)
            {
                var method = commandHandlerType.GetMethod("Handle")!;
                return (ValueTask<TResponse>)method.Invoke(commandHandler, new object[] { request, cancellationToken })!;
            }
        }

        // Otherwise resolve a query handler.
        var queryHandlerType = typeof(IQueryHandler<,>).MakeGenericType(requestType, typeof(TResponse));
        var queryHandler = Services.GetRequiredService(queryHandlerType);
        var queryMethod = queryHandlerType.GetMethod("Handle")!;
        return (ValueTask<TResponse>)queryMethod.Invoke(queryHandler, new object[] { request, cancellationToken })!;
    }

    /// <summary>
    /// Resolves a required service. Used by source-generated interceptors
    /// to obtain the concrete handler without reflection on the hot path.
    /// </summary>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>The resolved service instance.</returns>
    /// <exception cref="InvalidOperationException">No service of type <typeparamref name="T"/> is registered.</exception>
    public T GetRequiredService<T>() where T : notnull
        => ServiceProviderServiceExtensions.GetRequiredService<T>(Services);

    /// <summary>
    /// Resolves all registered services of type <typeparamref name="T"/>.
    /// Used by source-generated interceptors to obtain pipeline behaviors.
    /// </summary>
    /// <typeparam name="T">The service type to resolve.</typeparam>
    /// <returns>
    /// An enumerable of all services of type <typeparamref name="T"/>,
    /// or an empty sequence when none are registered.
    /// </returns>
    public IEnumerable<T> GetServices<T>()
    {
        try
        {
            return ServiceProviderServiceExtensions.GetServices<T>(Services);
        }
        catch (InvalidOperationException)
        {
            // No registration for T (or IEnumerable<T>) – treat as empty pipeline.
            return Array.Empty<T>();
        }
    }
}
