using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KetPsi.Mediator.Tests;

/// <summary>
/// Exercises concrete call sites that the source generator should intercept.
/// </summary>
public class InterceptorSmokeTests
{
    [Fact]
    public async Task ConcreteCommandCallSite_IsIntercepted()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<CreateOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new CreateOrderCommand("Intercepted", 1));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConcreteQueryCallSite_IsIntercepted()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var result = await mediator.Send(new GetOrderQuery(id));

        Assert.Equal(id, result.Id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ConcreteCommandWithResponse_IsIntercepted()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<PlaceOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var id = await mediator.Send(new PlaceOrderCommand("Intercepted-Cmd", 7));

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MultipleInterceptedCalls_EachInvokeHandler()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers();
        using var sp = services.BuildServiceProvider();
        var handler = sp.GetRequiredService<CreateOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new CreateOrderCommand("A", 1));
        await mediator.Send(new CreateOrderCommand("B", 2));
        await mediator.Send(new CreateOrderCommand("C", 3));

        Assert.Equal(3, handler.CallCount);
    }
}
