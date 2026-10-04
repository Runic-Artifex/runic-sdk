using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>Publishes indexed changes for a collection whose items have a stable, unique key.</summary>
/// <remarks>
/// Supports immutable replacements and property notifications on row objects.
/// Notifications below a row remain supported through full snapshots. Keys must not change
/// during a row's lifetime. Each presentation should own its viewport collection.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicCollectionAttribute(string keyProperty) : Attribute
{
    /// <summary>The item property containing a string, Guid or Int32 key.</summary>
    public string KeyProperty { get; } = keyProperty;
}

/// <summary>Generated, reflection-free metadata for an incremental collection.</summary>
public sealed record BridgeCollectionDescriptor<T>(
    string Name,
    Func<T, object?> Get,
    Action<Utf8JsonWriter, object?> WriteItem,
    Func<object?, string> Key);

internal sealed record BridgeCollectionChange<T>(
    BridgeCollectionDescriptor<T> Descriptor,
    string Kind,
    int Index,
    int OldIndex,
    string[] Keys,
    string[] Items);
