namespace KetPsi.Mediator;

/// <summary>
/// Dispatches requests (commands and queries) to their matching handlers.
/// </summary>
/// <remarks>
/// <para>
/// Call sites of <see cref="Send(ICommand, CancellationToken)"/> and
/// <see cref="Send{TResponse}(IRequest{TResponse}, CancellationToken)"/> are rewritten
/// at compile time by the KetPsi.Mediator source generator when the request type is
/// known statically. The rewritten call invokes the handler (and any pipeline behaviors)
/// directly, avoiding reflection.
/// </para>
/// <para>
/// When the request type is only known at runtime (for example stored in an
/// <see cref="ICommand"/> variable), the default <see cref="Mediator"/> implementation
/// falls back to a reflection-based resolution path.
/// </para>
/// </remarks>
public interface IMediator
{
    /// <summary>
    /// Sends a command that does not return a value.
    /// </summary>
    /// <param name="command">The command to dispatch.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>A <see cref="ValueTask"/> that completes when the command has been handled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    ValueTask Send(ICommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a request (command or query) that returns a response.
    /// </summary>
    /// <typeparam name="TResponse">The type of the value returned by the handler.</typeparam>
    /// <param name="request">The request to dispatch.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResponse}"/> that completes with the handler result.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
