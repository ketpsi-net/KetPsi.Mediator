using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KetPsi.Mediator.Tests;

public class MediatorTests
{
    private static ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        // Tests resolve from the root provider; use Singleton so that is valid.
        // Production web apps should call AddMediatorHandlers() (Scoped default) and resolve from a scope.
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    // ---------- Happy path: commands & queries ----------

    [Fact]
    public async Task Send_CommandWithoutResponse_CallsHandler()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<CreateOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new CreateOrderCommand("Widget", 3));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_CommandWithResponse_ReturnsValue()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<PlaceOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var id = await mediator.Send(new PlaceOrderCommand("Gadget", 2));

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_Query_ReturnsExpectedResult()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var orderId = Guid.NewGuid();
        var result = await mediator.Send(new GetOrderQuery(orderId));

        Assert.Equal(orderId, result.Id);
        Assert.Equal("Widget", result.Product);
        Assert.Equal(5, result.Quantity);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Send_MultipleDifferentRequests_WorkIndependently()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new CreateOrderCommand("A", 1));
        var id = await mediator.Send(new PlaceOrderCommand("B", 2));
        var order = await mediator.Send(new GetOrderQuery(id));
        var product = await mediator.Send(new GetProductQuery("SKU-1"));

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, order.Id);
        Assert.Equal("Product-SKU-1", product);
    }

    // ---------- Cancellation ----------

    [Fact]
    public async Task Send_CancelledToken_ThrowsOperationCanceled()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await mediator.Send(new CancelTestCommand(), cts.Token));
    }

    [Fact]
    public async Task Send_NotCancelledToken_CompletesNormally()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        using var cts = new CancellationTokenSource();
        await mediator.Send(new CancelTestCommand(), cts.Token);
    }

    // ---------- Null / argument validation ----------

    [Fact]
    public async Task Send_NullCommand_ThrowsArgumentNullException()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await mediator.Send((ICommand)null!));
    }

    [Fact]
    public async Task Send_NullRequest_ThrowsArgumentNullException()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await mediator.Send<OrderDto>(null!));
    }

    // ---------- DI registration ----------

    [Fact]
    public void AddMediatorHandlers_RegistersIMediator()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IMediator>());
        Assert.IsType<Mediator>(sp.GetService<IMediator>());
    }

    [Fact]
    public void AddMediatorHandlers_RegistersAllDiscoveredHandlers()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<ICommandHandler<CreateOrderCommand>>());
        Assert.NotNull(sp.GetService<ICommandHandler<PlaceOrderCommand, Guid>>());
        Assert.NotNull(sp.GetService<IQueryHandler<GetOrderQuery, OrderDto>>());
        Assert.NotNull(sp.GetService<IQueryHandler<GetProductQuery, string>>());
    }

    [Fact]
    public void AddMediatorHandlers_ConcreteAndInterface_ResolveToSameInstance()
    {
        // Shared-instance forwarding is independent of lifetime; use Singleton so
        // root provider resolution is valid without creating a scope.
        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        using var sp = services.BuildServiceProvider();

        var concrete = sp.GetRequiredService<CreateOrderHandler>();
        var viaInterface = sp.GetRequiredService<ICommandHandler<CreateOrderCommand>>();

        Assert.Same(concrete, viaInterface);
    }

    // ---------- Reflection fallback ----------

    [Fact]
    public async Task Fallback_WhenRequestTypedAsICommand_StillInvokesHandler()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<CreateOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        ICommand cmd = new CreateOrderCommand("Dynamic", 9);
        await mediator.Send(cmd);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Fallback_WhenRequestTypedAsIRequest_StillInvokesHandler()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        IRequest<OrderDto> request = new GetOrderQuery(Guid.NewGuid());
        var result = await mediator.Send(request);

        Assert.Equal("Widget", result.Product);
        Assert.Equal(1, handler.CallCount);
    }

    // ---------- Missing handler ----------

    [Fact]
    public async Task Send_NoHandlerRegistered_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMediator, Mediator>();
        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        ICommand cmd = new CreateOrderCommand("orphan", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await mediator.Send(cmd));
    }

    // ---------- ServiceLifetime ----------

    [Fact]
    public void AddMediatorHandlers_Scoped_ResolvesDifferentInstancesPerScope()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Scoped);
        using var sp = services.BuildServiceProvider();

        CreateOrderHandler a;
        CreateOrderHandler b;

        using (var scope1 = sp.CreateScope())
        {
            a = scope1.ServiceProvider.GetRequiredService<CreateOrderHandler>();
            var a2 = scope1.ServiceProvider.GetRequiredService<CreateOrderHandler>();
            Assert.Same(a, a2);
        }

        using (var scope2 = sp.CreateScope())
        {
            b = scope2.ServiceProvider.GetRequiredService<CreateOrderHandler>();
        }

        Assert.NotSame(a, b);
    }

    [Fact]
    public void AddMediatorHandlers_Scoped_MediatorIsScoped()
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Scoped);
        using var sp = services.BuildServiceProvider();

        IMediator m1, m2;
        using (var scope1 = sp.CreateScope())
            m1 = scope1.ServiceProvider.GetRequiredService<IMediator>();
        using (var scope2 = sp.CreateScope())
            m2 = scope2.ServiceProvider.GetRequiredService<IMediator>();

        Assert.NotSame(m1, m2);
    }

    // ---------- Zero-behavior fast path ----------

    [Fact]
    public async Task FastPath_QueryWithNoBehaviors_ReturnsHandlerResult()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<GetOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var id = Guid.NewGuid();
        var result = await mediator.Send(new GetOrderQuery(id));

        Assert.Equal(id, result.Id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FastPath_CommandWithResponse_NoBehaviors_ReturnsHandlerResult()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<PlaceOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        var id = await mediator.Send(new PlaceOrderCommand("Fast", 1));

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FastPath_VoidCommand_NoBehaviors_InvokesHandler()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<CreateOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new CreateOrderCommand("FastVoid", 1));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Fallback_WithBehavior_InvokesBehavior()
    {
        CountingBehavior<GetOrderQuery, OrderDto>.CallCount = 0;

        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IPipelineBehavior<GetOrderQuery, OrderDto>, CountingBehavior<GetOrderQuery, OrderDto>>());
        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        // Interface-typed variable forces the reflection fallback path
        IRequest<OrderDto> request = new GetOrderQuery(Guid.NewGuid());
        var result = await mediator.Send(request);

        Assert.Equal("Widget", result.Product);
        Assert.Equal(1, CountingBehavior<GetOrderQuery, OrderDto>.CallCount);
    }

    [Fact]
    public async Task Fallback_VoidCommand_WithUnitBehavior_InvokesBehavior()
    {
        UnitCountingBehavior.CallCount = 0;

        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        // UnitCountingBehavior may already be auto-registered; ensure at least one run
        using var sp = services.BuildServiceProvider();
        var mediator = sp.GetRequiredService<IMediator>();

        ICommand cmd = new CreateOrderCommand("fallback-unit", 1);
        await mediator.Send(cmd);

        Assert.True(UnitCountingBehavior.CallCount >= 1);
    }
}
