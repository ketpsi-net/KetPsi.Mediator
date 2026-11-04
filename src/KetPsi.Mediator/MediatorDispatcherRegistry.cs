using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace KetPsi.Mediator;

/// <summary>
/// Maps request types to strongly-typed dispatchers used by the <see cref="Mediator"/> fallback
/// (calls whose request type is not known statically, e.g. an <see cref="ICommand"/> variable).
/// </summary>
/// <remarks>
/// <para>
/// The generated <c>AddMediatorHandlers()</c> registers a dispatcher for every handler it discovers,
/// using closed generic types written out at compile time. That keeps the fallback free of
/// <see cref="Type.MakeGenericType"/>, so it works under Native AOT and trimming.
/// </para>
/// <para>
/// Call these methods yourself only for handlers registered manually in DI (for example private
/// nested handlers the generator cannot reference) that are sent through the fallback. Without a
/// registered dispatcher the fallback throws <see cref="InvalidOperationException"/>; it never
/// builds one by reflection.
/// </para>
/// </remarks>
public static class MediatorDispatcherRegistry
{
    private static readonly ConcurrentDictionary<Type, VoidCommandDispatcher> VoidCommandDispatchers = new();
    private static readonly ConcurrentDictionary<Type, ResponseDispatcher> ResponseDispatchers = new();

    /// <summary>Registers the fallback dispatcher for a command without a response.</summary>
    public static void AddCommand<TCommand>()
        where TCommand : ICommand
        => VoidCommandDispatchers.TryAdd(typeof(TCommand), new VoidCommandDispatcher<TCommand>());

    /// <summary>Registers the fallback dispatcher for a command with a response.</summary>
    public static void AddCommand<TCommand, TResponse>()
        where TCommand : ICommand<TResponse>
        => ResponseDispatchers.TryAdd(typeof(TCommand), new CommandDispatcher<TCommand, TResponse>());

    /// <summary>Registers the fallback dispatcher for a query.</summary>
    public static void AddQuery<TQuery, TResponse>()
        where TQuery : IQuery<TResponse>
        => ResponseDispatchers.TryAdd(typeof(TQuery), new QueryDispatcher<TQuery, TResponse>());

    internal static VoidCommandDispatcher GetCommandDispatcher(Type commandType)
    {
        return VoidCommandDispatchers.TryGetValue(commandType, out var dispatcher)
            ? dispatcher
            : throw MissingDispatcher(commandType);
    }

    internal static ResponseDispatcher GetResponseDispatcher(Type requestType)
    {
        return ResponseDispatchers.TryGetValue(requestType, out var dispatcher)
            ? dispatcher
            : throw MissingDispatcher(requestType);
    }

    private static InvalidOperationException MissingDispatcher(Type requestType) => new(
        $"No mediator dispatcher is registered for request type '{requestType}'. " +
        "Register its handler through the generated AddMediatorHandlers() method, or call " +
        $"{nameof(MediatorDispatcherRegistry)}.{nameof(AddQuery)}/{nameof(AddCommand)} for handlers registered manually.");
}

internal abstract class VoidCommandDispatcher
{
    public abstract ValueTask Send(Mediator mediator, ICommand command, CancellationToken cancellationToken);
}

internal sealed class VoidCommandDispatcher<TCommand> : VoidCommandDispatcher
    where TCommand : ICommand
{
    public override ValueTask Send(Mediator mediator, ICommand command, CancellationToken cancellationToken)
    {
        var handler = mediator.GetRequiredService<ICommandHandler<TCommand>>();
        var behaviors = mediator.GetServices<IPipelineBehavior<TCommand, Unit>>();
        return PipelineExecutor.ExecuteUnit((TCommand)command, behaviors, handler, cancellationToken);
    }
}

internal abstract class ResponseDispatcher
{
    /// <remarks>
    /// <typeparamref name="TResponse"/> is the type the caller asked for. It can differ from the
    /// handler's response type when <see cref="IRequest{TResponse}"/> covariance was used
    /// (e.g. <c>Send&lt;object&gt;(query returning string)</c>).
    /// </remarks>
    public abstract ValueTask<TResponse> Send<TResponse>(Mediator mediator, IRequest<TResponse> request, CancellationToken cancellationToken);

    protected static ValueTask<TResponse> Convert<TActual, TResponse>(ValueTask<TActual> result)
    {
        if (typeof(TActual) == typeof(TResponse))
            return Unsafe.As<ValueTask<TActual>, ValueTask<TResponse>>(ref result);

        return ConvertSlow<TActual, TResponse>(result);
    }

    private static async ValueTask<TResponse> ConvertSlow<TActual, TResponse>(ValueTask<TActual> result)
        => (TResponse)(object)(await result.ConfigureAwait(false))!;
}

internal sealed class QueryDispatcher<TQuery, TActual> : ResponseDispatcher
    where TQuery : IQuery<TActual>
{
    public override ValueTask<TResponse> Send<TResponse>(Mediator mediator, IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        var handler = mediator.GetRequiredService<IQueryHandler<TQuery, TActual>>();
        var behaviors = mediator.GetServices<IPipelineBehavior<TQuery, TActual>>();
        return Convert<TActual, TResponse>(PipelineExecutor.ExecuteQuery((TQuery)(object)request, behaviors, handler, cancellationToken));
    }
}

internal sealed class CommandDispatcher<TCommand, TActual> : ResponseDispatcher
    where TCommand : ICommand<TActual>
{
    public override ValueTask<TResponse> Send<TResponse>(Mediator mediator, IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        var handler = mediator.GetRequiredService<ICommandHandler<TCommand, TActual>>();
        var behaviors = mediator.GetServices<IPipelineBehavior<TCommand, TActual>>();
        return Convert<TActual, TResponse>(PipelineExecutor.ExecuteCommand((TCommand)(object)request, behaviors, handler, cancellationToken));
    }
}
