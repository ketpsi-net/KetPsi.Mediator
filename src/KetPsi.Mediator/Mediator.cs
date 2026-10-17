using System.Reflection;
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
/// The reflection fallback still runs registered <see cref="IPipelineBehavior{TRequest,TResponse}"/>
/// instances so cross-cutting concerns apply consistently.
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

        var requestType = command.GetType();
        var handlerType = typeof(ICommandHandler<>).MakeGenericType(requestType);
        var handler = Services.GetRequiredService(handlerType);

        // Build Func<TCommand, CancellationToken, ValueTask> via reflection, then run through Unit pipeline.
        var handleMethod = handlerType.GetMethod("Handle")!;
        var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(Unit));
        var behaviors = GetServicesOfType(behaviorType);

        return InvokeUnitPipeline(requestType, command, handler, handleMethod, behaviors, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        object handler;
        MethodInfo handleMethod;

        if (request is ICommand)
        {
            var commandHandlerType = typeof(ICommandHandler<,>).MakeGenericType(requestType, typeof(TResponse));
            handler = Services.GetRequiredService(commandHandlerType);
            handleMethod = commandHandlerType.GetMethod("Handle")!;
        }
        else
        {
            var queryHandlerType = typeof(IQueryHandler<,>).MakeGenericType(requestType, typeof(TResponse));
            handler = Services.GetRequiredService(queryHandlerType);
            handleMethod = queryHandlerType.GetMethod("Handle")!;
        }

        var behaviorType = typeof(IPipelineBehavior<,>).MakeGenericType(requestType, typeof(TResponse));
        var behaviors = GetServicesOfType(behaviorType);

        return InvokeResponsePipeline<TResponse>(requestType, request, handler, handleMethod, behaviors, cancellationToken);
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
    public IEnumerable<T> GetServices<T>()
    {
        try
        {
            return ServiceProviderServiceExtensions.GetServices<T>(Services);
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<T>();
        }
    }

    private IEnumerable<object> GetServicesOfType(Type serviceType)
    {
        try
        {
            var enumerableType = typeof(IEnumerable<>).MakeGenericType(serviceType);
            var result = Services.GetService(enumerableType);
            if (result is System.Collections.IEnumerable enumerable)
            {
                var list = new List<object>();
                foreach (var item in enumerable)
                {
                    if (item is not null)
                        list.Add(item);
                }
                return list;
            }
        }
        catch (InvalidOperationException)
        {
            // treat as empty
        }

        return Array.Empty<object>();
    }

    private static ValueTask<TResponse> InvokeResponsePipeline<TResponse>(
        Type requestType,
        object request,
        object handler,
        MethodInfo handleMethod,
        IEnumerable<object> behaviors,
        CancellationToken cancellationToken)
    {
        var behaviorList = behaviors as IList<object> ?? behaviors.ToList();

        // Fast path: no behaviors → call handler directly
        if (behaviorList.Count == 0)
            return (ValueTask<TResponse>)handleMethod.Invoke(handler, new object[] { request, cancellationToken })!;

        // Build chain from the end (handler) toward the first behavior.
        // Each behavior.Handle(request, next, ct) where next is RequestHandlerDelegate<TResponse>.
        var delegateType = typeof(RequestHandlerDelegate<>).MakeGenericType(typeof(TResponse));

        // Terminal: call the real handler
        RequestHandlerDelegate<TResponse> next = () =>
            (ValueTask<TResponse>)handleMethod.Invoke(handler, new object[] { request, cancellationToken })!;

        var handleBehaviorMethod = typeof(IPipelineBehavior<,>)
            .MakeGenericType(requestType, typeof(TResponse))
            .GetMethod("Handle")!;

        for (int i = behaviorList.Count - 1; i >= 0; i--)
        {
            var behavior = behaviorList[i];
            var current = next;
            next = () =>
            {
                var result = handleBehaviorMethod.Invoke(behavior, new object[] { request, current, cancellationToken });
                return (ValueTask<TResponse>)result!;
            };
        }

        return next();
    }

    private static ValueTask InvokeUnitPipeline(
        Type requestType,
        object request,
        object handler,
        MethodInfo handleMethod,
        IEnumerable<object> behaviors,
        CancellationToken cancellationToken)
    {
        var behaviorList = behaviors as IList<object> ?? behaviors.ToList();

        if (behaviorList.Count == 0)
            return (ValueTask)handleMethod.Invoke(handler, new object[] { request, cancellationToken })!;

        // Reuse response pipeline with Unit adapter
        RequestHandlerDelegate<Unit> next = async () =>
        {
            await ((ValueTask)handleMethod.Invoke(handler, new object[] { request, cancellationToken })!)
                .ConfigureAwait(false);
            return Unit.Value;
        };

        var handleBehaviorMethod = typeof(IPipelineBehavior<,>)
            .MakeGenericType(requestType, typeof(Unit))
            .GetMethod("Handle")!;

        for (int i = behaviorList.Count - 1; i >= 0; i--)
        {
            var behavior = behaviorList[i];
            var current = next;
            next = () =>
            {
                var result = handleBehaviorMethod.Invoke(behavior, new object[] { request, current, cancellationToken });
                return (ValueTask<Unit>)result!;
            };
        }

        return AwaitUnit(next);
    }

    private static async ValueTask AwaitUnit(RequestHandlerDelegate<Unit> next)
    {
        await next().ConfigureAwait(false);
    }
}
