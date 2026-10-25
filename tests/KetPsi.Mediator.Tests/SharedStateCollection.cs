using Xunit;

namespace KetPsi.Mediator.Tests;

/// <summary>
/// Test classes that reset or assert on static behavior counters (e.g. <see cref="UnitCountingBehavior.CallCount"/>)
/// must not run in parallel with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedStateCollection
{
    public const string Name = "Shared static behavior state";
}
