namespace KetPsi.Mediator;

/// <summary>
/// Represents the next step in a pipeline (either another behavior or the final handler).
/// </summary>
/// <typeparam name="TResponse">The type of the value returned by the pipeline.</typeparam>
/// <returns>A <see cref="ValueTask{TResponse}"/> that completes with the pipeline result.</returns>
public delegate ValueTask<TResponse> RequestHandlerDelegate<TResponse>();

/// <summary>
/// A cross-cutting pipeline behavior that runs around a request handler.
/// </summary>
/// <typeparam name="TRequest">The request type this behavior applies to.</typeparam>
/// <typeparam name="TResponse">The response type produced by the handler.</typeparam>
/// <remarks>
/// <para>
/// Behaviors are executed in registration order. Each behavior receives a
/// <see cref="RequestHandlerDelegate{TResponse}"/> that represents the remainder
/// of the pipeline (subsequent behaviors + the final handler).
/// </para>
/// <para>
/// Typical uses: logging, validation, transactions, caching, performance timing.
/// </para>
/// <para>
/// For commands that do not return a value, use
/// <see cref="IPipelineBehavior{TRequest, TResponse}"/> with
/// <see cref="Unit"/> as <typeparamref name="TResponse"/>.
/// </para>
/// </remarks>
public interface IPipelineBehavior<in TRequest, TResponse>
{
    /// <summary>
    /// Processes the request, optionally calling <paramref name="next"/> to continue the pipeline.
    /// </summary>
    /// <param name="request">The request being handled.</param>
    /// <param name="next">
    /// The remainder of the pipeline. Call this delegate to invoke subsequent behaviors
    /// and eventually the handler. You may choose not to call it (for example to short-circuit).
    /// </param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResponse}"/> that completes with the final pipeline result.
    /// </returns>
    ValueTask<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken);
}
