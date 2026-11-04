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
/// The fallback looks up the strongly-typed dispatcher that the generated
/// <c>AddMediatorHandlers()</c> registered for the request type (see
/// <see cref="MediatorDispatcherRegistry"/>) and runs it through <see cref="PipelineExecutor"/>,
/// so registered <see cref="IPipelineBehavior{TRequest,TResponse}"/> instances apply, exceptions
/// propagate exactly as they do on the intercepted path, and no code is generated at runtime
/// (Native AOT and trimming safe).
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

        return MediatorDispatcherRegistry.GetCommandDispatcher(command.GetType()).Send(this, command, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MediatorDispatcherRegistry.GetResponseDispatcher(request.GetType()).Send(this, request, cancellationToken);
    }

    /// <summary>
    /// Resolves a required service. Used by source-generated interceptors
    /// to obtain the concrete handler without reflection on the hot path.
    /// </summary>
    public T GetRequiredService<T>() where T : notnull
        => ServiceProviderServiceExtensions.GetRequiredService<T>(Services);

    /// <summary>
    /// Resolves all registered services of type <typeparamref name="T"/>.
    /// Used by source-generated interceptors to obtain pipeline behaviors.
    /// </summary>
    /// <remarks>
    /// Returns an empty sequence when nothing is registered. Activation failures
    /// (for example a behavior with an unregistered dependency) are not swallowed,
    /// so a misconfigured behavior can never be silently skipped.
    /// </remarks>
    public IEnumerable<T> GetServices<T>()
        => ServiceProviderServiceExtensions.GetServices<T>(Services);
}
