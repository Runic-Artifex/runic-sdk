namespace Runic.Application.Views;

/// <summary>Controls how an observable ReactiveUI command result is retained.</summary>
public enum BridgeCommandResultCardinality
{
    /// <summary>Require exactly one observable result.</summary>
    Single,

    /// <summary>Retain the final observable result after successful completion.</summary>
    Last,

    /// <summary>Expose every result through the bounded cursor stream protocol.</summary>
    Stream,
}

/// <summary>
/// Opts a generated command contract into a non-default result cardinality.
/// ReactiveUI commands with <c>RxVoid</c> have no result and do not need this
/// attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class RunicCommandResultAttribute(BridgeCommandResultCardinality cardinality) : Attribute
{
    public BridgeCommandResultCardinality Cardinality { get; } = cardinality;
}
