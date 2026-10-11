namespace KetPsi.Mediator.Tests;

// ---------- Commands (no response) ----------

/// <summary>Creates an order. Fire-and-forget.</summary>
public sealed record CreateOrderCommand(string Product, int Quantity) : ICommand;

public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand>
{
    public int CallCount;

    public ValueTask Handle(CreateOrderCommand command, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return ValueTask.CompletedTask;
    }
}

// ---------- Commands with response ----------

/// <summary>Places an order and returns the new order id.</summary>
public sealed record PlaceOrderCommand(string Product, int Quantity) : ICommand<Guid>;

public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Guid>
{
    public int CallCount;

    public ValueTask<Guid> Handle(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return ValueTask.FromResult(Guid.NewGuid());
    }
}

// ---------- Queries ----------
public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;
public sealed record GetOrderQueryWithBehavior(Guid OrderId) : IQuery<OrderDto>;

public sealed record OrderDto(Guid Id, string Product, int Quantity);

public sealed class GetOrderWithBehaviorHandler : IQueryHandler<GetOrderQueryWithBehavior, OrderDto>
{
    public int CallCount;

    public ValueTask<OrderDto> Handle(GetOrderQueryWithBehavior query, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return ValueTask.FromResult(new OrderDto(query.OrderId, "Widget", 5));
    }
}

public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    public int CallCount;

    public ValueTask<OrderDto> Handle(GetOrderQuery query, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return ValueTask.FromResult(new OrderDto(query.OrderId, "Widget", 5));
    }
}

public sealed record GetProductQuery(string Sku) : IQuery<string>;

public sealed class GetProductHandler : IQueryHandler<GetProductQuery, string>
{
    public ValueTask<string> Handle(GetProductQuery query, CancellationToken cancellationToken = default)
        => ValueTask.FromResult($"Product-{query.Sku}");
}

// ---------- Cancellation ----------

public sealed record CancelTestCommand : ICommand;

public sealed class CancelTestHandler : ICommandHandler<CancelTestCommand>
{
    public ValueTask Handle(CancelTestCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

// ---------- Pipeline behaviors ----------

/// <summary>Increments a static counter each time it runs.</summary>
public sealed class CountingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    public static int CallCount;

    public async ValueTask<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return await next().ConfigureAwait(false);
    }
}

/// <summary>Records the order in which behaviors execute (before/after).</summary>
public sealed class OrderedBehaviorA : IPipelineBehavior<GetOrderQueryWithBehavior, OrderDto>
{
    public static List<string> Log { get; } = new();

    public async ValueTask<OrderDto> Handle(
        GetOrderQueryWithBehavior request,
        RequestHandlerDelegate<OrderDto> next,
        CancellationToken cancellationToken)
    {
        Log.Add("A:before");
        var result = await next().ConfigureAwait(false);
        Log.Add("A:after");
        return result;
    }
}

public sealed class OrderedBehaviorB : IPipelineBehavior<GetOrderQueryWithBehavior, OrderDto>
{
    public async ValueTask<OrderDto> Handle(
        GetOrderQueryWithBehavior request,
        RequestHandlerDelegate<OrderDto> next,
        CancellationToken cancellationToken)
    {
        OrderedBehaviorA.Log.Add("B:before");
        var result = await next().ConfigureAwait(false);
        OrderedBehaviorA.Log.Add("B:after");
        return result;
    }
}


/// <summary>Throws when the request product name is empty (validation example).</summary>
public sealed class ValidationBehavior : IPipelineBehavior<PlaceOrderCommand, Guid>
{
    public ValueTask<Guid> Handle(
        PlaceOrderCommand request,
        RequestHandlerDelegate<Guid> next,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Product))
            throw new ArgumentException("Product must not be empty.", nameof(request));

        return next();
    }
}

/// <summary>Behavior for void commands (Unit response).</summary>
public sealed class UnitCountingBehavior : IPipelineBehavior<CreateOrderCommand, Unit>
{
    public static int CallCount;

    public async ValueTask<Unit> Handle(
        CreateOrderCommand request,
        RequestHandlerDelegate<Unit> next,
        CancellationToken cancellationToken)
    {
        CallCount++;
        return await next().ConfigureAwait(false);
    }
}
