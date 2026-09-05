using System;

namespace Runic.Application.Bridge;

/// <summary>Declares the one member-authored Application Bridge contract in an application assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class ApplicationBridgeContractAttribute(string identity, int version) : Attribute
{
    /// <summary>Gets the stable protocol identity.</summary>
    public string Identity { get; } = identity;
    /// <summary>Gets the protocol version.</summary>
    public int Version { get; } = version;
    /// <summary>Gets the generated contract name.</summary>
    public string ContractName { get; init; } = "Application";
}

/// <summary>Marks the property or method that supplies the authoritative session snapshot.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BridgeSnapshotAttribute : Attribute;

/// <summary>Marks a method as an Application Bridge command handler.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BridgeCommandAttribute : Attribute
{
    /// <summary>Gets whether the command starts a backend-owned operation.</summary>
    public bool StartsOperation { get; init; }
    /// <summary>Gets whether the started operation accepts cancellation.</summary>
    public bool Cancellable { get; init; }
    /// <summary>Gets whether successful dispatch advances the session revision.</summary>
    public bool AdvancesRevision { get; init; }
}

/// <summary>Marks a payload type as a bridge event.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BridgeEventAttribute : Attribute;

/// <summary>Marks a payload type as a declared application error.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BridgeErrorAttribute : Attribute;

/// <summary>Overrides the discriminator used for a command, receipt, event, or error.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BridgeTagAttribute(string value) : Attribute
{
    /// <summary>Gets the stable wire identifier.</summary>
    public string Value { get; } = value;
}

/// <summary>Overrides the stable named-reference identifier used in Bridge IR.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BridgeNameAttribute(string value) : Attribute
{
    /// <summary>Gets the stable wire identifier.</summary>
    public string Value { get; } = value;
}

/// <summary>Constrains a wire number to the inclusive lower bound.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeMinimumAttribute(double value) : Attribute
{
    /// <summary>Gets the constraint value.</summary>
    public double Value { get; } = value;
}
/// <summary>Constrains a wire number to the inclusive upper bound.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeMaximumAttribute(double value) : Attribute
{
    /// <summary>Gets the constraint value.</summary>
    public double Value { get; } = value;
}
/// <summary>Constrains a wire number to the exclusive lower bound.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeExclusiveMinimumAttribute(double value) : Attribute
{
    /// <summary>Gets the constraint value.</summary>
    public double Value { get; } = value;
}
/// <summary>Constrains a wire number to the exclusive upper bound.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeExclusiveMaximumAttribute(double value) : Attribute
{
    /// <summary>Gets the constraint value.</summary>
    public double Value { get; } = value;
}
/// <summary>Constrains a wire number to the positive numeric multiple.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeMultipleOfAttribute(double value) : Attribute
{
    /// <summary>Gets the constraint value.</summary>
    public double Value { get; } = value;
}
/// <summary>Constrains the UTF-16 string length.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeStringLengthAttribute(int minimum = 0, int maximum = int.MaxValue) : Attribute
{
    /// <summary>Gets the inclusive minimum length.</summary>
    public int Minimum { get; } = minimum;
    /// <summary>Gets the inclusive maximum length.</summary>
    public int Maximum { get; } = maximum;
}
/// <summary>Constrains a string using a portable regular expression.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgePatternAttribute(string pattern) : Attribute
{
    /// <summary>Gets the portable regular expression.</summary>
    public string Pattern { get; } = pattern;
}
/// <summary>Constrains the number of collection items.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeCollectionLengthAttribute(int minimum = 0, int maximum = int.MaxValue) : Attribute
{
    /// <summary>Gets the inclusive minimum length.</summary>
    public int Minimum { get; } = minimum;
    /// <summary>Gets the inclusive maximum length.</summary>
    public int Maximum { get; } = maximum;
}
/// <summary>Requires structurally unique collection items.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeUniqueAttribute : Attribute;
/// <summary>Restricts 64-bit integers to the interoperable JSON safe-integer range.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class BridgeSafeIntegerAttribute : Attribute;

/// <summary>Safe infrastructure supplied while producing an initialization or reconnection snapshot.</summary>
public sealed class BridgeSnapshotContext
{
    internal BridgeSnapshotContext(BridgeSessionId sessionId, long currentRevision)
    {
        SessionId = sessionId;
        CurrentRevision = currentRevision;
    }

    /// <summary>Gets the logical session identifier.</summary>
    public BridgeSessionId SessionId { get; }
    /// <summary>Gets the authoritative revision at snapshot admission.</summary>
    public long CurrentRevision { get; }
}
