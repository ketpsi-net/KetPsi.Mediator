namespace KetPsi.Mediator;

/// <summary>
/// A void-like value used so that fire-and-forget commands can share the same
/// pipeline infrastructure as requests that return a result.
/// </summary>
/// <remarks>
/// Similar in purpose to MediatR.Unit. Pipeline behaviors for commands without a
/// response are typed as <see cref="IPipelineBehavior{TRequest, TResponse}"/> with
/// <see cref="Unit"/> as the response type.
/// </remarks>
public readonly struct Unit : IEquatable<Unit>
{
    /// <summary>
    /// The single <see cref="Unit"/> value.
    /// </summary>
    public static readonly Unit Value = default;

    /// <inheritdoc />
    public bool Equals(Unit other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Unit;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <inheritdoc />
    public override string ToString() => "()";

    /// <summary>
    /// Always returns <see langword="true"/>; every <see cref="Unit"/> is equal to every other.
    /// </summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>
    /// Always returns <see langword="false"/>.
    /// </summary>
    public static bool operator !=(Unit left, Unit right) => false;
}
