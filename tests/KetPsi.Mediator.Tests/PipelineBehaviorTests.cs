using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KetPsi.Mediator.Tests;

public class PipelineBehaviorTests
{
    [Fact]
    public async Task SingleBehavior_IsInvokedAroundHandler()
    {
        CountingBehavior<GetOrderQuery, OrderDto>.CallCount = 0;

        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPipelineBehavior<GetOrderQuery, OrderDto>, CountingBehavior<GetOrderQuery, OrderDto>>());
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new GetOrderQuery(Guid.NewGuid()));

        Assert.Equal(1, CountingBehavior<GetOrderQuery, OrderDto>.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MultipleBehaviors_ExecuteInRegistrationOrder()
    {
        OrderedBehaviorA.Log.Clear();

        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        // OrderedBehaviorA and OrderedBehaviorB are discovered by the generator
        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new GetOrderQueryWithBehavior(Guid.NewGuid()));

        // Order depends on discovery order; both must appear as before/after pairs
        Assert.Contains("A:before", OrderedBehaviorA .Log);
        Assert.Contains("A:after", OrderedBehaviorA.Log);
        Assert.Contains("B:before", OrderedBehaviorA.Log);
        Assert.Contains("B:after", OrderedBehaviorA.Log);
        Assert.Equal(4, OrderedBehaviorA.Log.Count);
    }

    private sealed class ShortCircuitBehavior : IPipelineBehavior<GetOrderQueryWithBehavior, OrderDto>
    {
        public ValueTask<OrderDto> Handle(
            GetOrderQueryWithBehavior request,
            RequestHandlerDelegate<OrderDto> next,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new OrderDto(Guid.Empty, "short-circuited", 0));
    }

    [Fact]
    public async Task Behavior_CanShortCircuit_WithoutCallingHandler()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        // Nested class is not discovered by the generator – register manually
        services.TryAddEnumerable(ServiceDescriptor.    Singleton<IPipelineBehavior<GetOrderQueryWithBehavior, OrderDto>, ShortCircuitBehavior>());
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetOrderQueryWithBehavior(Guid.NewGuid()));

        Assert.Equal("short-circuited", result.Product);
        Assert.Equal(0, handler.CallCount); // handler never reached
    }

    [Fact]
    public async Task Behavior_CanValidate_AndThrow()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        // ValidationBehavior is closed and discovered by the generator

        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await mediator.Send(new PlaceOrderCommand("", 1)));
    }

    [Fact]
    public async Task Behavior_ValidRequest_PassesThrough()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<PlaceOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var id = await mediator.Send(new PlaceOrderCommand("Valid", 1));

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task UnitBehavior_RunsAroundVoidCommand()
    {
        UnitCountingBehavior.CallCount = 0;

        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        // UnitCountingBehavior is discovered & registered by the generator
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<CreateOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new CreateOrderCommand("X", 1));

        Assert.Equal(1, UnitCountingBehavior.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NoBehaviorsRegistered_HandlerStillRuns()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        // no behaviors
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetOrderQuery(Guid.NewGuid()));

        Assert.Equal("Widget", result.Product);
        Assert.Equal(1, handler.CallCount);
    }
}
