using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace KetPsi.Mediator.Tests;

// ---------- Fixtures ----------

public sealed class HandlerFailedException : Exception;

public sealed record ThrowingCommand : ICommand;

public sealed class ThrowingCommandHandler : ICommandHandler<ThrowingCommand>
{
    // Throws synchronously (not inside an async method) – the case reflection used to wrap.
    public ValueTask Handle(ThrowingCommand command, CancellationToken cancellationToken = default)
        => throw new HandlerFailedException();
}

public sealed record PingCommand : ICommand;

public sealed record PingCountQuery : IQuery<int>;

/// <summary>One class implementing two handler interfaces.</summary>
public sealed class MultiRequestHandler : ICommandHandler<PingCommand>, IQueryHandler<PingCountQuery, int>
{
    public int Pings;

    public ValueTask Handle(PingCommand command, CancellationToken cancellationToken = default)
    {
        Pings++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> Handle(PingCountQuery query, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Pings);
}

public sealed record EchoQuery<T>(T Value) : IQuery<T>;

public sealed class IntEchoHandler : IQueryHandler<EchoQuery<int>, int>
{
    public ValueTask<int> Handle(EchoQuery<int> query, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(query.Value);
}

/// <summary>A hand-written IMediator, e.g. a decorator or a test double.</summary>
public sealed class RecordingMediator : IMediator
{
    public List<object> Sent { get; } = new();

    public ValueTask Send(ICommand command, CancellationToken cancellationToken = default)
    {
        Sent.Add(command);
        return ValueTask.CompletedTask;
    }

    public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Sent.Add(request);
        return ValueTask.FromResult(default(TResponse)!);
    }
}

public sealed class MissingDependency;

[Collection(SharedStateCollection.Name)]
public class RegressionTests
{
    private static ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddMediatorHandlers(ServiceLifetime.Singleton);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Fallback_CommandWithResponse_TypedAsIRequest_ResolvesCommandHandler()
    {
        using var sp = BuildServices();
        var handler = sp.GetRequiredService<PlaceOrderHandler>();
        var mediator = sp.GetRequiredService<IMediator>();

        IRequest<Guid> request = new PlaceOrderCommand("Dynamic", 1);
        var id = await mediator.Send(request);

        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Fallback_HandlerException_IsNotWrapped()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        ICommand command = new ThrowingCommand();

        await Assert.ThrowsAsync<HandlerFailedException>(async () => await mediator.Send(command));
    }

    [Fact]
    public async Task Intercepted_HandlerException_IsNotWrapped()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<HandlerFailedException>(async () => await mediator.Send(new ThrowingCommand()));
    }

    [Fact]
    public async Task Fallback_CovariantResponse_ResolvesHandlerByDeclaredResponse()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        IRequest<object> request = new GetProductQuery("cov");
        var result = await mediator.Send(request);

        Assert.Equal("Product-cov", result);
    }

    [Fact]
    public async Task CovariantCallSite_FallsBackAndResolvesHandler()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        var result = await mediator.Send<object>(new GetProductQuery("cov-site"));

        Assert.Equal("Product-cov-site", result);
    }

    [Fact]
    public async Task InterceptedCallSite_WithCustomMediator_DelegatesToIt()
    {
        IMediator mediator = new RecordingMediator();

        await mediator.Send(new CreateOrderCommand("custom", 1));
        await mediator.Send(new GetOrderQuery(Guid.Empty));

        Assert.Equal(2, ((RecordingMediator)mediator).Sent.Count);
    }

    [Fact]
    public async Task CallSite_OnConcreteMediatorReceiver_Works()
    {
        using var sp = BuildServices();
        var mediator = (Mediator)sp.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetProductQuery("concrete"));
        await mediator.Send(new CreateOrderCommand("concrete", 1));

        Assert.Equal("Product-concrete", result);
    }

    [Fact]
    public async Task InterceptedCallSite_NullRequest_ThrowsArgumentNullException()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        CreateOrderCommand? command = null;
        GetOrderQuery? query = null;

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Send(command!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await mediator.Send(query!));
    }

    [Fact]
    public async Task NamedArguments_AreSupported()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        var result = await mediator.Send(cancellationToken: CancellationToken.None, request: new GetProductQuery("named"));

        Assert.Equal("Product-named", result);
    }

    [Fact]
    public async Task Behavior_WithMissingDependency_IsNotSilentlySkipped()
    {
        using var sp = BuildServices(s =>
            s.TryAddEnumerable(ServiceDescriptor.Singleton<IPipelineBehavior<GetProductQuery, string>, NeedsMissingDependencyBehavior>()));
        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await mediator.Send(new GetProductQuery("x")));
    }

    [Fact]
    public async Task HandlerImplementingSeveralInterfaces_IsRegisteredForEach()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        await mediator.Send(new PingCommand());
        await mediator.Send(new PingCommand());
        var count = await mediator.Send(new PingCountQuery());

        Assert.Equal(2, count);
        Assert.Same(sp.GetRequiredService<ICommandHandler<PingCommand>>(), sp.GetRequiredService<IQueryHandler<PingCountQuery, int>>());
    }

    [Fact]
    public async Task CallSite_InGenericMethod_FallsBackInsteadOfBreakingTheBuild()
    {
        using var sp = BuildServices();
        var mediator = sp.GetRequiredService<IMediator>();

        Assert.Equal(42, await SendEcho(mediator, 42));
    }

    [Fact]
    public async Task CallSite_WithPrivateRequestType_FallsBackInsteadOfBreakingTheBuild()
    {
        using var sp = BuildServices(s => s.AddSingleton<IQueryHandler<PrivateQuery, int>, PrivateQueryHandler>());
        var mediator = sp.GetRequiredService<IMediator>();

        Assert.Equal(7, await mediator.Send(new PrivateQuery()));
    }

    [Fact]
    public void AssemblySpecificRegistrationMethod_IsGenerated()
    {
        var services = new ServiceCollection();
        services.AddKetPsiMediatorTestsMediatorHandlers(ServiceLifetime.Singleton);
        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IQueryHandler<PingCountQuery, int>>());
    }

    [Fact]
    public void AddKetPsiMediator_DefaultsToScoped()
    {
        var services = new ServiceCollection();
        services.AddKetPsiMediator();

        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IMediator)).Lifetime);
    }

    private static ValueTask<T> SendEcho<T>(IMediator mediator, T value)
        => mediator.Send(new EchoQuery<T>(value));

    private sealed record PrivateQuery : IQuery<int>;

    private sealed class PrivateQueryHandler : IQueryHandler<PrivateQuery, int>
    {
        public ValueTask<int> Handle(PrivateQuery query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(7);
    }

    private sealed class NeedsMissingDependencyBehavior(MissingDependency dependency) : IPipelineBehavior<GetProductQuery, string>
    {
        public ValueTask<string> Handle(GetProductQuery request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            _ = dependency;
            return next();
        }
    }
}
