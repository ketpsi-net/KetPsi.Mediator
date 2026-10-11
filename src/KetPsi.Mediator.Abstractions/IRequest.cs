namespace KetPsi.Mediator;

/// <summary>
/// Marker interface for every request that can be sent through the mediator.
/// Both commands and queries implement this interface.
/// </summary>
public interface IRequest
{
}

/// <summary>
/// Marker interface for a request that produces a typed response.
/// </summary>
/// <typeparam name="TResponse">The type of the value returned by the handler.</typeparam>
public interface IRequest<out TResponse> : IRequest
{
}

/// <summary>
/// A command that performs work and does not return a value.
/// </summary>
/// <remarks>
/// Use <see cref="ICommand{TResponse}"/> when the command must return a result
/// (for example a newly created entity id).
/// </remarks>
public interface ICommand : IRequest
{
}

/// <summary>
/// A command that performs work and returns a value.
/// </summary>
/// <typeparam name="TResponse">The type of the value returned by the handler.</typeparam>
public interface ICommand<out TResponse> : IRequest<TResponse>
{
}

/// <summary>
/// A query that retrieves data and returns a typed response.
/// Queries are expected to be side-effect free.
/// </summary>
/// <typeparam name="TResponse">The type of the value returned by the handler.</typeparam>
public interface IQuery<out TResponse> : IRequest<TResponse>
{
}
