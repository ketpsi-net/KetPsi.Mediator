namespace KetPsi.Mediator;

/// <summary>
/// Handles a query and returns a typed response.
/// </summary>
/// <typeparam name="TQuery">The concrete query type this handler processes.</typeparam>
/// <typeparam name="TResponse">The type of the value returned by the handler.</typeparam>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    /// <summary>
    /// Executes the query and returns its result.
    /// </summary>
    /// <param name="query">The query instance to process.</param>
    /// <param name="cancellationToken">
    /// A token that can be used to cancel the operation.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResponse}"/> that completes with the query result.
    /// </returns>
    ValueTask<TResponse> Handle(TQuery query, CancellationToken cancellationToken = default);
}
