namespace KetPsi.Mediator;

/// <summary>
/// Handles a command that does not produce a response.
/// </summary>
/// <typeparam name="TCommand">The concrete command type this handler processes.</typeparam>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="command">The command instance to process.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>A <see cref="ValueTask"/> that completes when the command has been handled.</returns>
    ValueTask Handle(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles a command that produces a typed response.
/// </summary>
/// <typeparam name="TCommand">The concrete command type this handler processes.</typeparam>
/// <typeparam name="TResponse">The type of the value returned by the handler.</typeparam>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    /// <summary>
    /// Executes the command and returns its result.
    /// </summary>
    /// <param name="command">The command instance to process.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResponse}"/> that completes with the command result.
    /// </returns>
    ValueTask<TResponse> Handle(TCommand command, CancellationToken cancellationToken = default);
}
