namespace KetPsi.Mediator;

/// <summary>
/// Builds and runs the pipeline behavior chain around a request handler.
/// </summary>
/// <remarks>
/// Called by source-generated interceptors. The chain is built from the last
/// registered behavior toward the first, so behaviors execute in registration order.
/// </remarks>
public static class PipelineExecutor
{
    /// <summary>
    /// Executes a request through zero or more pipeline behaviors and a final handler.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request instance to process.</param>
    /// <param name="behaviors">
    /// The pipeline behaviors to run, in the order they should execute.
    /// May be empty.
    /// </param>
    /// <param name="handler">
    /// The final handler that produces the response when the pipeline reaches the end.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResponse}"/> that completes with the pipeline result.
    /// </returns>
    public static ValueTask<TResponse> Execute<TRequest, TResponse>(
        TRequest request,
        IEnumerable<IPipelineBehavior<TRequest, TResponse>> behaviors,
        Func<TRequest, CancellationToken, ValueTask<TResponse>> handler,
        CancellationToken cancellationToken)
    {
        // Materialize once so registration order is stable and enumeration is cheap.
        var list = behaviors as IList<IPipelineBehavior<TRequest, TResponse>>
                   ?? behaviors.ToList();

        if (list.Count == 0)
            return handler(request, cancellationToken);

        // Build the chain from the end (handler) toward the first behavior.
        RequestHandlerDelegate<TResponse> next = () => handler(request, cancellationToken);

        for (int i = list.Count - 1; i >= 0; i--)
        {
            var behavior = list[i];
            var current = next;
            next = () => behavior.Handle(request, current, cancellationToken);
        }

        return next();
    }

    /// <summary>
    /// Adapter for fire-and-forget commands (no response).
    /// Wraps the command handler so it can participate in
    /// <see cref="IPipelineBehavior{TRequest, TResponse}"/> with
    /// <see cref="Unit"/> as the response type.
    /// </summary>
    /// <typeparam name="TRequest">The command type.</typeparam>
    /// <param name="request">The command instance to process.</param>
    /// <param name="behaviors">
    /// Pipeline behaviors typed against <see cref="Unit"/>.
    /// </param>
    /// <param name="handler">The final command handler.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>A <see cref="ValueTask"/> that completes when the pipeline finishes.</returns>
    public static async ValueTask ExecuteUnit<TRequest>(
        TRequest request,
        IEnumerable<IPipelineBehavior<TRequest, Unit>> behaviors,
        Func<TRequest, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken)
    {
        await Execute(
            request,
            behaviors,
            async (r, ct) =>
            {
                await handler(r, ct).ConfigureAwait(false);
                return Unit.Value;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
