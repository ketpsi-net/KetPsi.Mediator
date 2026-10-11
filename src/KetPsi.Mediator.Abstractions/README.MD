# KetPsi.Mediator 

[![NuGet](https://img.shields.io/nuget/v/KetPsi.Mediator.svg)](https://www.nuget.org/packages/KetPsi.Mediator/)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)

**KetPsi.Mediator** is a high-performance, AOT-friendly mediator for .NET powered by **C# interceptors** and **Roslyn source generators**.

Call sites of `IMediator.Send(...)` are rewritten at compile time to invoke the matching handler **directly**. No reflection, no dictionary lookups, no runtime type resolution on the hot path.

Inspired by the same techniques used in [KetPsi.Logging.Extensions](https://github.com/ketpsi-net/KetPsi.Logging.Extensions).

---

## Why

Traditional mediators (MediatR, etc.) resolve handlers via `IServiceProvider` + reflection or a runtime dictionary on every call. That works, but it costs:

- Type lookups
- Virtual/interface dispatch through the mediator
- Extra allocations in some pipelines

KetPsi.Mediator keeps the same ergonomic API while making the common case a direct method call.

## Packages

| Package | Purpose |
|---------|---------|
| `KetPsi.Mediator.Abstractions` | `IMediator`, `ICommand`, `IQuery`, handler interfaces |
| `KetPsi.Mediator` | Default `Mediator` implementation + DI extensions |
| `KetPsi.Mediator.Generator` | Roslyn source generator (interceptors + handler registration) |

## Quick Start

### 1. Install

```bash
dotnet add package KetPsi.Mediator
dotnet add package KetPsi.Mediator.Generator
```

### 2. Enable interceptors in your project

```xml
<PropertyGroup>
  <InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);KetPsi.Mediator</InterceptorsPreviewNamespaces>
</PropertyGroup>
```

### 3. Define requests & handlers

```csharp
public sealed record CreateOrderCommand(string Product, int Quantity) : ICommand;

public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand>
{
    public ValueTask Handle(CreateOrderCommand command, CancellationToken ct = default)
    {
        // business logic
        return ValueTask.CompletedTask;
    }
}

public sealed record GetOrderQuery(Guid Id) : IQuery<OrderDto>;
public sealed record OrderDto(Guid Id, string Product);

public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    public ValueTask<OrderDto> Handle(GetOrderQuery query, CancellationToken ct = default)
        => ValueTask.FromResult(new OrderDto(query.Id, "Widget"));
}
```

### 4. Register

```csharp
builder.Services.AddMediatorHandlers(); // generated – registers IMediator + all handlers + behaviors
```

### 5. Use

```csharp
await mediator.Send(new CreateOrderCommand("Widget", 3));
var order = await mediator.Send(new GetOrderQuery(id));
```

The generator rewrites the concrete call sites above into direct handler invocations.

## How it works

1. The source generator scans the compilation for types implementing `ICommandHandler<>` / `IQueryHandler<,>`.
2. It emits `AddMediatorHandlers()` that registers every discovered handler with DI.
3. It finds every `mediator.Send(...)` call site whose request type is a concrete type.
4. For each such call site it emits an interceptor method marked with `[InterceptsLocation]`.
5. The C# compiler rewrites the original call to the generated interceptor, which resolves the handler once and calls `Handle` directly.

When the request type is not known statically (e.g. stored in an `ICommand` variable), the original runtime path (reflection fallback inside `Mediator`) is used.

## Features

- **Zero-reflection hot path** for concrete request types
- **Compile-time handler discovery** and DI registration
- **Single registration call** – `AddMediatorHandlers()` registers `IMediator`, handlers, and behaviors
- **Pipeline behaviors** (`IPipelineBehavior<TRequest, TResponse>`)
- **AOT compatible** (no runtime code generation)
- **Commands** (no response / with response) and **Queries**
- **CancellationToken** support
- **Fallback** path for dynamic request types
- Same structure and design philosophy as KetPsi.Logging.Extensions

## Project structure

```
KetPsi.Mediator/
├── src/
│   ├── KetPsi.Mediator.Abstractions/   # Interfaces
│   ├── KetPsi.Mediator/               # Runtime + DI
│   └── KetPsi.Mediator.Generator/     # Source generator
├── tests/
│   └── KetPsi.Mediator.Tests/         # Comprehensive unit tests
└── README.md
```

## License

MIT
