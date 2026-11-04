using System.ComponentModel;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>Publishes indexed changes for a collection whose items have a stable, unique key.</summary>
/// <remarks>
/// Supports immutable replacements and property notifications on row objects.
/// Notifications below a row remain supported through full snapshots. Keys must not change
/// during a row's lifetime. A list too large to publish whole is a <see cref="RunicQueryCollection{TRow, TKey}"/>, whose
/// rows each presentation reads for its own range.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicCollectionAttribute(string keyProperty) : Attribute
{
    /// <summary>The item property containing a string, Guid, Int32 or Int64 (<see langword="long"/>) key.</summary>
    public string KeyProperty { get; } = keyProperty;
}

internal sealed record BridgeCollectionChange<T>(
    BridgeCollectionDescriptor<T> Descriptor,
    string Kind,
    int Index,
    int OldIndex,
    string[] Keys,
    byte[][] Items);
