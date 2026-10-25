# KetPsi.Mediator

[![NuGet](https://img.shields.io/nuget/v/KetPsi.Mediator.svg)](https://www.nuget.org/packages/KetPsi.Mediator/)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)

**KetPsi.Mediator** is a high-performance, AOT-friendly mediator for .NET powered by **C# interceptors** and **Roslyn source generators**.

Call sites of `IMediator.Send(...)` are rewritten at compile time to invoke the matching handler **directly** through dependency injection—with **zero reflection**, **zero dictionary lookups**, and **zero delegate or async state-machine allocations** on the no-behavior hot path.

Inspired by the same compile-time design philosophy used in [KetPsi.Logging.Extensions](https://github.com/ketpsi-net/KetPsi.Logging.Extensions).

---

## Why KetPsi.Mediator?

Traditional mediators (such as MediatR) resolve handlers and wrappers via `IServiceProvider` + reflection or runtime dictionaries on every call. Other source-generated mediators generate a monolithic switch table at the composition root, requiring all handlers and requests to be visible in a single project.

**KetPsi.Mediator** takes a different approach:
- **Call-Site Interception:** Uses C# `[InterceptsLocation]` to rewrite `mediator.Send(new MyCommand(...))` at the exact call site.
- **Zero-Allocation Fast Path:** When no pipeline behaviors are registered for a request, `PipelineExecutor` invokes `handler.Handle(...)` directly without allocating wrapper delegates (`Func<>`), lists, or `async` state machines.
- **First-Class Multi-Project Architecture:** Interceptors inspect the request type's interfaces (`ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>`) directly at the call site and resolve `ICommandHandler<>` / `IQueryHandler<,>` from DI. Your `Services` or `Presentation` project never needs compile-time access to concrete handler classes in your `Application` project.
- **Scoped by Default:** DI registrations default to `ServiceLifetime.Scoped` (configurable to `Singleton` or `Transient`), making handlers safe to use with EF Core `DbContext` and scoped unit-of-work services out of the box.
- **Test-Friendly:** Intercepted call sites only take the fast path when the receiver is the built-in `Mediator`. Mocks, fakes, and decorators of `IMediator` keep working unchanged.

---

## Packages

| Package | Purpose |
|---|---|
| `KetPsi.Mediator.Abstractions` | Core contracts (`IMediator`, `ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>`, handler & behavior interfaces, `Unit`) |
| `KetPsi.Mediator` | Runtime `Mediator` (fallback implementation), `PipelineExecutor`, manual DI extensions, **and the bundled source generator** (interceptors + `AddMediatorHandlers()`) |

> The source generator ships inside the `KetPsi.Mediator` package. There is no separate generator package to install.

---

## Quick Start

### 1. Install the Package

```bash
dotnet add package KetPsi.Mediator
```

*(Pure contracts projects that only declare commands, queries, and DTOs can reference `KetPsi.Mediator.Abstractions` instead.)*

### 2. Enable Interceptors in Projects Calling `IMediator.Send`

Add `InterceptorsPreviewNamespaces` to every project that invokes `mediator.Send(...)`:

```xml
<PropertyGroup>
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Mediator</InterceptorsPreviewNamespaces>
</PropertyGroup>
```

### 3. Define Requests & Handlers

```csharp
using KetPsi.Mediator;

// 1. Fire-and-forget Command (no response)
public sealed record CreateOrderCommand(string Product, int Quantity) : ICommand;

public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand>
{
    public ValueTask Handle(CreateOrderCommand command, CancellationToken ct = default)
    {
        // Business logic
        return ValueTask.CompletedTask;
    }
}

// 2. Command with Response
public sealed record PlaceOrderCommand(string Product, int Quantity) : ICommand<Guid>;

public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrderCommand, Guid>
{
    public ValueTask<Guid> Handle(PlaceOrderCommand command, CancellationToken ct = default)
        => ValueTask.FromResult(Guid.NewGuid());
}

// 3. Query
public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;
public sealed record OrderDto(Guid Id, string Product, int Quantity);

public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> Handle(GetOrderQuery query, CancellationToken ct = default)
        => ValueTask.FromResult(new OrderDto(query.OrderId, "Widget", 5));
}
```

A single class may implement several handler interfaces. It is registered once and resolved for each of them.

### 4. Register with Dependency Injection

Call the generated `AddMediatorHandlers()` extension method in your composition root:

```csharp
// Defaults to ServiceLifetime.Scoped (recommended for ASP.NET Core / EF Core)
builder.Services.AddMediatorHandlers();

// Or specify a custom lifetime:
builder.Services.AddMediatorHandlers(ServiceLifetime.Singleton);
```

### 5. Dispatch Requests

```csharp
await mediator.Send(new CreateOrderCommand("Widget", 3), ct);

Guid orderId = await mediator.Send(new PlaceOrderCommand("Gadget", 2), ct);

OrderDto order = await mediator.Send(new GetOrderQuery(orderId), ct);
```

---

## Pipeline Behaviors

Implement `IPipelineBehavior<TRequest, TResponse>` to add cross-cutting concerns such as validation, logging, metrics, or transactions. For void commands (`ICommand`), use `Unit` as `TResponse`.

```csharp
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
```

- **Closed generic behaviors** (e.g., `IPipelineBehavior<PlaceOrderCommand, Guid>` or `IPipelineBehavior<CreateOrderCommand, Unit>`) declared as top-level types are automatically discovered and registered by `AddMediatorHandlers()`.
- **Open generic behaviors** (e.g., `LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>`) and nested behavior classes must be registered explicitly with DI:

```csharp
builder.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
```

- Behaviors run in registration order (first registered = outermost).
- If a registered behavior cannot be constructed (for example a missing dependency), `Send` throws. Behaviors are never silently skipped.

---

## Multi-Project Architecture

`KetPsi.Mediator` is designed for clean architecture and modular monoliths:

- **`Application` Project** (contains Commands, Queries, and Handlers):
  - Reference `KetPsi.Mediator`.
  - The generator emits `AddMediatorHandlers()` for all handlers and closed behaviors in `Application`.
- **`Services` / `WebApi` Project** (calls `IMediator.Send`):
  - Reference `KetPsi.Mediator` and enable `<InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Mediator</InterceptorsPreviewNamespaces>`.
  - The generator emits `[InterceptsLocation]` methods for every concrete `mediator.Send(...)` call site without needing access to the handler classes in `Application`. Because `Services` has no handlers of its own, the generator will not emit a duplicate `AddMediatorHandlers()` extension class.

### Several projects with handlers (modular monoliths)

Each project that contains handlers also gets an **assembly-specific** registration method named `Add{AssemblyName}MediatorHandlers()`. Non-identifier characters are removed from the assembly name and each segment is capitalized. When the composition root references more than one handler project, `AddMediatorHandlers()` becomes ambiguous (CS0121). Call the assembly-specific methods instead:

```csharp
// Orders.Application.dll and Billing.Application.dll both contain handlers
builder.Services.AddOrdersApplicationMediatorHandlers();
builder.Services.AddBillingApplicationMediatorHandlers();
```

---

## Dynamic Dispatch Fallback

When the concrete request type is not known statically at compile time, the call is not intercepted and falls back to `Mediator.Send(...)`. Examples are a polymorphic `ICommand` or `IRequest<TResponse>` variable, a request type that depends on a generic type parameter, or a private nested request type.

```csharp
ICommand dynamicCommand = GetNextCommand();
await mediator.Send(dynamicCommand, ct);
```

The fallback builds one strongly-typed dispatcher per request type and caches it. It then runs the same `PipelineExecutor` as the intercepted path, so:
- `ICommand`, `ICommand<TResponse>`, and `IQuery<TResponse>` resolve the correct handler interface.
- Registered `IPipelineBehavior<,>` instances run in the same order.
- Exceptions thrown by handlers and behaviors propagate unwrapped, exactly as on the intercepted path.
- Covariant calls such as `mediator.Send<object>(new GetProductQuery(...))` resolve the handler for the request's declared response type.

---

## Diagnostics

| ID | Severity | Meaning |
|---|---|---|
| `KPM001` | Warning | More than one handler implements the same request in a project. Only the first is registered by `AddMediatorHandlers()`. |

---

## Notes & Limitations

- Interceptors cast to the built-in `Mediator` only when the receiver *is* one. Other `IMediator` implementations receive the original `Send` call.
- `AddKetPsiMediator()` (manual registration of `IMediator` only) defaults to `ServiceLifetime.Scoped`, consistent with `AddMediatorHandlers()`.
- Interceptors currently use the file-path form of `[InterceptsLocation]` for compatibility with the .NET 8 SDK.

---

## License

MIT License — see [LICENSE](LICENSE) for details.
