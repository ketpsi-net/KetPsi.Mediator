using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
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
/// The fallback builds one strongly-typed dispatcher per request type (cached for the
/// lifetime of the process) and runs it through <see cref="PipelineExecutor"/>, so
/// registered <see cref="IPipelineBehavior{TRequest,TResponse}"/> instances apply and
/// exceptions propagate exactly as they do on the intercepted path.
/// </para>
/// </remarks>
public sealed partial class Mediator : IMediator
{
    private static readonly ConcurrentDictionary<Type, VoidCommandDispatcher> VoidCommandDispatchers = new();

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

        var dispatcher = VoidCommandDispatchers.GetOrAdd(command.GetType(), CreateVoidCommandDispatcher);
        return dispatcher.Send(this, command, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dispatcher = ResponseDispatcherCache<TResponse>.Dispatchers.GetOrAdd(request.GetType(), CreateResponseDispatcher<TResponse>);
        return dispatcher.Send(this, request, cancellationToken);
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

    private static VoidCommandDispatcher CreateVoidCommandDispatcher(Type commandType)
        => (VoidCommandDispatcher)Activator.CreateInstance(
            typeof(VoidCommandDispatcher<>).MakeGenericType(commandType))!;

    private static ResponseDispatcher<TResponse> CreateResponseDispatcher<TResponse>(Type requestType)
    {
        // Pick the IQuery<T> / ICommand<T> the request actually implements. T may differ from
        // TResponse when IRequest<out TResponse> covariance was used at the call site
        // (e.g. Send<object>(query returning string)); handlers are keyed by T.
        Type? match = null;
        Type? matchDefinition = null;

        foreach (var iface in requestType.GetInterfaces())
        {
            if (!iface.IsGenericType)
                continue;

            var definition = iface.GetGenericTypeDefinition();
            if (definition != typeof(IQuery<>) && definition != typeof(ICommand<>))
                continue;

            var actual = iface.GetGenericArguments()[0];
            if (!typeof(TResponse).IsAssignableFrom(actual))
                continue;

            match = actual;
            matchDefinition = definition;
            if (actual == typeof(TResponse))
                break;
        }

        if (match is null)
        {
            throw new InvalidOperationException(
                $"Request type '{requestType}' must implement ICommand<{typeof(TResponse).Name}> or IQuery<{typeof(TResponse).Name}> to be sent through the mediator.");
        }

        var dispatcherDefinition = matchDefinition == typeof(IQuery<>)
            ? typeof(QueryDispatcher<,,>)
            : typeof(CommandDispatcher<,,>);

        return (ResponseDispatcher<TResponse>)Activator.CreateInstance(
            dispatcherDefinition.MakeGenericType(requestType, match, typeof(TResponse)))!;
    }

    private static class ResponseDispatcherCache<TResponse>
    {
        public static readonly ConcurrentDictionary<Type, ResponseDispatcher<TResponse>> Dispatchers = new();
    }

    private abstract class VoidCommandDispatcher
    {
        public abstract ValueTask Send(Mediator mediator, ICommand command, CancellationToken cancellationToken);
    }

    private sealed class VoidCommandDispatcher<TCommand> : VoidCommandDispatcher
        where TCommand : ICommand
    {
        public override ValueTask Send(Mediator mediator, ICommand command, CancellationToken cancellationToken)
        {
            var handler = mediator.GetRequiredService<ICommandHandler<TCommand>>();
            var behaviors = mediator.GetServices<IPipelineBehavior<TCommand, Unit>>();
            return PipelineExecutor.ExecuteUnit((TCommand)command, behaviors, handler, cancellationToken);
        }
    }

    private abstract class ResponseDispatcher<TResponse>
    {
        public abstract ValueTask<TResponse> Send(Mediator mediator, IRequest<TResponse> request, CancellationToken cancellationToken);

        protected static ValueTask<TResponse> Convert<TActual>(ValueTask<TActual> result)
        {
            if (typeof(TActual) == typeof(TResponse))
                return Unsafe.As<ValueTask<TActual>, ValueTask<TResponse>>(ref result);

            return ConvertSlow(result);
        }

        private static async ValueTask<TResponse> ConvertSlow<TActual>(ValueTask<TActual> result)
            => (TResponse)(object)(await result.ConfigureAwait(false))!;
    }

    private sealed class QueryDispatcher<TQuery, TActual, TResponse> : ResponseDispatcher<TResponse>
        where TQuery : IQuery<TActual>
    {
        public override ValueTask<TResponse> Send(Mediator mediator, IRequest<TResponse> request, CancellationToken cancellationToken)
        {
            var handler = mediator.GetRequiredService<IQueryHandler<TQuery, TActual>>();
            var behaviors = mediator.GetServices<IPipelineBehavior<TQuery, TActual>>();
            return Convert(PipelineExecutor.ExecuteQuery((TQuery)request, behaviors, handler, cancellationToken));
        }
    }

    private sealed class CommandDispatcher<TCommand, TActual, TResponse> : ResponseDispatcher<TResponse>
        where TCommand : ICommand<TActual>
    {
        public override ValueTask<TResponse> Send(Mediator mediator, IRequest<TResponse> request, CancellationToken cancellationToken)
        {
            var handler = mediator.GetRequiredService<ICommandHandler<TCommand, TActual>>();
            var behaviors = mediator.GetServices<IPipelineBehavior<TCommand, TActual>>();
            return Convert(PipelineExecutor.ExecuteCommand((TCommand)request, behaviors, handler, cancellationToken));
        }
    }
}
