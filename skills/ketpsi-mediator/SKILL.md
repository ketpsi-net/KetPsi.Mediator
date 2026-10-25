---
name: ketpsi-mediator
description: How to use the KetPsi.Mediator NuGet packages (compile-time, interceptor-based mediator for .NET 8/9/10) — defining commands, queries, handlers and pipeline behaviors, DI registration with AddMediatorHandlers / Add{Assembly}MediatorHandlers, multi-project setup, testing with mocked IMediator, and pitfalls. Use when code references KetPsi.Mediator, ICommand/IQuery/ICommandHandler/IQueryHandler/IPipelineBehavior from the KetPsi.Mediator namespace, AddMediatorHandlers, the KPM001 diagnostic, or when the user asks to add CQRS/mediator handlers to a project using this package or to migrate from MediatR to it.
---

# Using KetPsi.Mediator (1.1+)

KetPsi.Mediator is a mediator whose `IMediator.Send(...)` calls are rewritten at compile time by a Roslyn source generator using C# interceptors (`[InterceptsLocation]`). When the request type is known at the call site, the call goes straight to the handler resolved from DI. There is no reflection, no dictionary lookup, and nothing is allocated when no behaviors are registered.

## Packages

| Package | Reference it from |
|---|---|
| `KetPsi.Mediator.Abstractions` | Pure contracts projects that only declare requests/DTOs |
| `KetPsi.Mediator` | Projects with handlers and/or projects that call `Send`. **Bundles the source generator.** |

```bash
dotnet add package KetPsi.Mediator
```

There is **no** `KetPsi.Mediator.Generator` package. Do not add one, and remove any such reference you find.

All public types live in the `KetPsi.Mediator` namespace.

## Required project setting

Every project that **calls** `mediator.Send(...)` must opt into the interceptor namespace, otherwise compilation fails with an interceptors-not-enabled error:

```xml
<PropertyGroup>
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Mediator</InterceptorsPreviewNamespaces>
</PropertyGroup>
```

## Request types

Pick the interface by intent. The handler interface must match the request interface:

| Request | Handler | Pipeline `TResponse` |
|---|---|---|
| `ICommand` (no result) | `ICommandHandler<TCommand>` → `ValueTask` | `Unit` |
| `ICommand<TResponse>` | `ICommandHandler<TCommand, TResponse>` → `ValueTask<TResponse>` | `TResponse` |
| `IQuery<TResponse>` (side-effect free) | `IQueryHandler<TQuery, TResponse>` → `ValueTask<TResponse>` | `TResponse` |

```csharp
using KetPsi.Mediator;

public sealed record CreateOrderCommand(string Product, int Quantity) : ICommand;

public sealed class CreateOrderHandler(AppDbContext db) : ICommandHandler<CreateOrderCommand>
{
    public async ValueTask Handle(CreateOrderCommand command, CancellationToken ct = default)
    {
        db.Orders.Add(new Order(command.Product, command.Quantity));
        await db.SaveChangesAsync(ct);
    }
}

public sealed record PlaceOrderCommand(string Product, int Quantity) : ICommand<Guid>;

public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Guid>
{
    public ValueTask<Guid> Handle(PlaceOrderCommand command, CancellationToken ct = default)
        => ValueTask.FromResult(Guid.NewGuid());
}

public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;

public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> Handle(GetOrderQuery query, CancellationToken ct = default)
        => ValueTask.FromResult(new OrderDto(query.OrderId, "Widget", 5));
}
```

Conventions:
- Handlers return `ValueTask`/`ValueTask<T>`, not `Task`. Return synchronously with `ValueTask.CompletedTask` or `ValueTask.FromResult(...)` when no I/O is needed.
- Handlers may be `class`, `record`, or `struct`. A single class may implement several handler interfaces; it is registered once and resolved for each interface.
- Auto-registered handlers must be concrete, non-generic, and accessible at assembly level (`public` or `internal`). Private nested handlers and open generic handlers are skipped, so register them manually.
- Keep one handler per request. If a project has two, the generator reports **KPM001** and registers only the first.
- Request types should be concrete (`sealed record` is idiomatic).

## Registration

Every project that declares handlers or closed pipeline behaviors gets two generated extension methods:

```csharp
builder.Services.AddMediatorHandlers();                     // Scoped (default)
builder.Services.AddMediatorHandlers(ServiceLifetime.Singleton);

// Assembly-specific equivalent: "Add" + assembly name without dots/dashes + "MediatorHandlers"
builder.Services.AddMyAppApplicationMediatorHandlers();     // for MyApp.Application.dll
```

Each method registers:
- `IMediator → Mediator`
- every handler, both as its concrete type and as each of its handler interfaces, sharing one instance
- every closed, top-level `IPipelineBehavior<TRequest, TResponse>`

All of these use the lifetime you pass in, and all registrations are `TryAdd`, so whatever is registered first wins.

Use Scoped (the default) whenever handlers depend on `DbContext` or other scoped services. `AddKetPsiMediator()` (which registers only `IMediator`) also defaults to Scoped.

## Dispatching

```csharp
public sealed class OrdersController(IMediator mediator) : ControllerBase
{
    [HttpPost]
    public async Task<Guid> Place(PlaceOrderCommand cmd, CancellationToken ct)
        => await mediator.Send(cmd, ct);

    [HttpGet("{id}")]
    public async Task<OrderDto> Get(Guid id, CancellationToken ct)
        => await mediator.Send(new GetOrderQuery(id), ct);
}
```

A call is intercepted (fast path) only when the argument's **static type is a concrete, accessible, non-generic-parameter type** and its `IQuery<T>`/`ICommand<T>` response type matches the `Send<T>` type argument exactly:

```csharp
await mediator.Send(new CreateOrderCommand("A", 1), ct);  // intercepted
ICommand cmd = GetNext();
await mediator.Send(cmd, ct);                             // fallback (interface-typed)
await mediator.Send(new EchoQuery<T>(value), ct);         // fallback (depends on T)
await mediator.Send<object>(new GetProductQuery("x"));    // fallback (covariant response)
```

The fallback (`Mediator.Send`) caches a strongly-typed dispatcher per request type. It resolves the right handler, runs the same behaviors, and propagates the same exceptions as the intercepted path; it is only slower. In hot paths, keep the request's concrete type visible at the call site.

## Pipeline behaviors

```csharp
public sealed class ValidatePlaceOrder : IPipelineBehavior<PlaceOrderCommand, Guid>
{
    public ValueTask<Guid> Handle(PlaceOrderCommand request, RequestHandlerDelegate<Guid> next, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Product))
            throw new ArgumentException("Product must not be empty.", nameof(request));
        return next();
    }
}

// Void commands use Unit as TResponse
public sealed class AuditCreateOrder : IPipelineBehavior<CreateOrderCommand, Unit>
{
    public async ValueTask<Unit> Handle(CreateOrderCommand request, RequestHandlerDelegate<Unit> next, CancellationToken ct)
    {
        var result = await next();
        // audit...
        return result;
    }
}
```

- **Closed**, top-level behaviors are auto-registered by `AddMediatorHandlers()`.
- **Open generic** and **nested** behaviors must be registered manually:
  ```csharp
  builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
  ```
- Behaviors run in DI registration order, first registered = outermost. A behavior short-circuits by returning without calling `next()`.
- A registered behavior that cannot be constructed (for example a missing dependency) makes `Send` throw `InvalidOperationException`. It is never silently skipped.

## Multi-project (clean architecture) setup

```
Contracts            → KetPsi.Mediator.Abstractions     (requests, DTOs)
Orders.Application   → KetPsi.Mediator                  (handlers) → AddOrdersApplicationMediatorHandlers()
Billing.Application  → KetPsi.Mediator                  (handlers) → AddBillingApplicationMediatorHandlers()
WebApi               → KetPsi.Mediator + InterceptorsPreviewNamespaces  (calls Send)
```

```csharp
// WebApi composition root
builder.Services.AddOrdersApplicationMediatorHandlers();
builder.Services.AddBillingApplicationMediatorHandlers();
```

- Calling projects don't need compile-time access to handler classes. Interceptors resolve handlers from DI using only the request type.
- With **one** handler project, `AddMediatorHandlers()` is fine.
- With **several** handler projects referenced by one root, `AddMediatorHandlers()` is ambiguous (CS0121). Call each project's `Add{Assembly}MediatorHandlers()` instead.
- A project with no handlers or behaviors does not emit either method.

## Testing

- Mocking `IMediator` (Moq, NSubstitute, hand-written fakes, decorators) works. An intercepted call site forwards to the receiver's own `Send` when the receiver is not the built-in `Mediator`.
- For integration-style tests, build a `ServiceCollection`, call `AddMediatorHandlers(ServiceLifetime.Singleton)` when resolving from the root provider, or keep Scoped and resolve from `CreateScope()`.

## Pitfalls

- **Missing `InterceptorsPreviewNamespaces`** in a project that calls `Send` causes a build error. Add it to that project, not only to the handler project.
- **Missing handler at runtime** throws `InvalidOperationException` from DI. Check that the handler's project registration method is called, and that the handler isn't private, nested-private, or open generic (those are skipped).
- **Singleton mediator with scoped handlers:** passing `ServiceLifetime.Singleton` while handlers depend on scoped services captures them in the root provider. Keep the default Scoped for web apps.
- **KPM001:** two handlers for one request. Remove one; DI registers only the first.
- To inspect what was generated, set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` and look in `obj/<Config>/<tfm>/generated/KetPsi.Mediator.Generator/`.

## Migrating from MediatR

| MediatR | KetPsi.Mediator |
|---|---|
| `IRequest` / `IRequestHandler<T>` | `ICommand` / `ICommandHandler<T>` |
| `IRequest<T>` (writes) | `ICommand<T>` / `ICommandHandler<T, TResp>` |
| `IRequest<T>` (reads) | `IQuery<T>` / `IQueryHandler<T, TResp>` |
| `Task<T> Handle(...)` | `ValueTask<T> Handle(...)` |
| `IPipelineBehavior<TReq,TResp>` + `RequestHandlerDelegate<TResp>` | Same names and parameter order. `next()` takes no arguments and returns `ValueTask<TResp>` |
| `services.AddMediatR(...)` | `services.AddMediatorHandlers()` (or `Add{Assembly}MediatorHandlers()` per project) |
| `Unit` (MediatR) | `KetPsi.Mediator.Unit` (only in behaviors for void commands) |

Notifications (`INotification`/`Publish`) and streaming requests are not provided.
