namespace KetPsi.Mediator;

/// <summary>
/// Builds and runs the pipeline behavior chain around a request handler.
/// </summary>
/// <remarks>
/// Prefer the handler-interface overloads (<see cref="ExecuteQuery{TQuery,TResponse}"/>,
/// <see cref="ExecuteCommand{TCommand,TResponse}"/>, <see cref="ExecuteUnit{TCommand}"/>)
/// from generated interceptors — they avoid allocating a <see cref="Func{T,TResult}"/>
/// delegate on every <c>Send</c> and short-circuit without an async state machine when
/// no behaviors are registered.
/// </remarks>
public static class PipelineExecutor
{
    // -------------------------------------------------------------------------
    // Zero-allocation overloads used by the source generator
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes a query through zero or more pipeline behaviors and a query handler.
    /// </summary>
    /// <remarks>
    /// When <paramref name="behaviors"/> is empty, returns
    /// <c>handler.Handle(...)</c> directly with no list, closure, or state-machine allocation.
    /// </remarks>
    public static ValueTask<TResponse> ExecuteQuery<TQuery, TResponse>(
        TQuery query,
        IEnumerable<IPipelineBehavior<TQuery, TResponse>> behaviors,
        IQueryHandler<TQuery, TResponse> handler,
        CancellationToken cancellationToken)
        where TQuery : IQuery<TResponse>
    {
        if (IsEmpty(behaviors))
            return handler.Handle(query, cancellationToken);

        return ExecuteCore(query, Materialize(behaviors), handler.Handle, cancellationToken);
    }

    /// <summary>
    /// Executes a command that returns a response through zero or more pipeline behaviors.
    /// </summary>
    public static ValueTask<TResponse> ExecuteCommand<TCommand, TResponse>(
        TCommand command,
        IEnumerable<IPipelineBehavior<TCommand, TResponse>> behaviors,
        ICommandHandler<TCommand, TResponse> handler,
        CancellationToken cancellationToken)
        where TCommand : ICommand<TResponse>
    {
        if (IsEmpty(behaviors))
            return handler.Handle(command, cancellationToken);

        return ExecuteCore(command, Materialize(behaviors), handler.Handle, cancellationToken);
    }

    /// <summary>
    /// Executes a fire-and-forget command through zero or more pipeline behaviors.
    /// </summary>
    /// <remarks>
    /// The outer method is intentionally <strong>not</strong> <c>async</c>.
    /// When there are no behaviors, returns <c>handler.Handle(...)</c> synchronously
    /// with no state-machine allocation. The slow path uses a private async helper.
    /// </remarks>
    public static ValueTask ExecuteUnit<TCommand>(
        TCommand command,
        IEnumerable<IPipelineBehavior<TCommand, Unit>> behaviors,
        ICommandHandler<TCommand> handler,
        CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        if (IsEmpty(behaviors))
            return handler.Handle(command, cancellationToken);

        return ExecuteUnitSlow(command, Materialize(behaviors), handler, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Backward-compatible Func<> overloads
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes a request through zero or more pipeline behaviors and a final handler delegate.
    /// Prefer the handler-interface overloads from generated code to avoid delegate allocation.
    /// </summary>
    public static ValueTask<TResponse> Execute<TRequest, TResponse>(
        TRequest request,
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors,
        Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
        CancellationToken cancellationToken)
    {
        if (IsEmpty(behaviors))
            return handler(request, cancellationToken);

        return ExecuteCore(request, Materialize(behaviors), handler, cancellationToken);
    }

    /// <summary>
    /// Adapter for fire-and-forget commands using a <see cref="Func{T,TResult}"/> handler.
    /// Prefer <see cref="ExecuteUnit{TCommand}(TCommand, IEnumerable{IPipelineBehavior{TCommand, Unit}}, ICommandHandler{TCommand}, CancellationToken)"/>
    /// from generated code.
    /// </summary>
    public static ValueTask ExecuteUnit<TRequest>(
        TRequest request,
        IEnumerable<IPipelineBehavior<TRequest, Unit>> behaviors,
        Func<TRequest, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        if (IsEmpty(behaviors))
            return handler(request, cancellationToken);

        return ExecuteUnitSlowFunc(request, Materialize(behaviors), handler, cancellationToken);
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private static bool IsEmpty<T>(IEnumerable<T> behaviors) =>
        behaviors is ICollection<T> { Count: 0 };

    private static IList<IPipelineBehavior<TRequest, TResponse>> Materialize<TRequest, TResponse>(
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors) =>
        behaviors as IList<IPipelineBehavior<TRequest, TResponse>>
        ?? behaviors.ToList();

    private static ValueTask<TResponse> ExecuteCore<TRequest, TResponse>(
        TRequest request,
        IList<IPipelineBehavior<TRequest, TResponse>> list,
        Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
        CancellationToken cancellationToken)
    {
        RequestHandlerDelegate<TResponse> next = () => handler(request, cancellationToken);

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var behavior = list[i];
            var current = next;
            next = () => behavior.Handle(request, current, cancellationToken);
        }

        return next();
    }

    private static async ValueTask ExecuteUnitSlow<TCommand>(
        TCommand command,
        IList<IPipelineBehavior<TCommand, Unit>> list,
        ICommandHandler<TCommand> handler,
        CancellationToken cancellationToken)
        where TCommand : ICommand
    {
        await ExecuteCore(
            command,
            list,
            async (r, ct) =>
            {
                await handler.Handle(r, ct).ConfigureAwait(false);
                return Unit.Value;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExecuteUnitSlowFunc<TRequest>(
        TRequest request,
        IList<IPipelineBehavior<TRequest, Unit>> list,
        Func<TRequest, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        await ExecuteCore(
            request,
            list,
            async (r, ct) =>
            {
                await handler(r, ct).ConfigureAwait(false);
                return Unit.Value;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
