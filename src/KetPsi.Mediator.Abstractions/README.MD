# KetPsi.Mediator.Abstractions

[![NuGet](https://img.shields.io/nuget/v/KetPsi.Mediator.Abstractions.svg)](https://www.nuget.org/packages/KetPsi.Mediator.Abstractions/)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)

Core interfaces and contracts for **KetPsi.Mediator**, a high-performance mediator for .NET powered by C# interceptors and Roslyn source generators.

Reference this lightweight, zero-dependency package in shared contract or domain projects that define commands, queries, handler interfaces, or pipeline behaviors without needing the runtime DI container or source generator.

---

## Included Types

- **`IMediator`** — Dispatches commands (`Send(ICommand, CancellationToken)`) and requests with responses (`Send<TResponse>(IRequest<TResponse>, CancellationToken)`).
- **`IRequest` / `IRequest<out TResponse>`** — Base marker interfaces for all mediator requests.
- **`ICommand` / `ICommand<out TResponse>`** — Marker interfaces for fire-and-forget commands and commands that return a value.
- **`IQuery<out TResponse>`** — Marker interface for side-effect-free queries returning a typed response.
- **`ICommandHandler<in TCommand>` / `ICommandHandler<in TCommand, TResponse>`** — Handler contracts for commands.
- **`IQueryHandler<in TQuery, TResponse>`** — Handler contract for queries.
- **`IPipelineBehavior<in TRequest, TResponse>` & `RequestHandlerDelegate<TResponse>`** — Cross-cutting middleware pipeline contract around handlers.
- **`Unit`** — Lightweight void-like value type (`IEquatable<Unit>`) allowing fire-and-forget commands (`ICommand`) to participate in `IPipelineBehavior<TCommand, Unit>`.

---

## Usage

```csharp
using KetPsi.Mediator;

public sealed record CreateOrderCommand(string Product, int Quantity) : ICommand;

public sealed record PlaceOrderCommand(string Product, int Quantity) : ICommand<Guid>;

public sealed record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;
public sealed record OrderDto(Guid Id, string Product, int Quantity);
```

For full runtime execution and compile-time call-site interception, install [`KetPsi.Mediator`](https://www.nuget.org/packages/KetPsi.Mediator/), which bundles the source generator.