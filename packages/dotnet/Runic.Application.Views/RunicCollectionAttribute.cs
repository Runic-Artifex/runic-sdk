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

/// <summary>Generated, reflection-free metadata for a keyed collection.</summary>
/// <remarks>
/// The bridge checks the keys of every state and frame it writes. With a null row or a null, empty
/// or duplicate key it withholds the route's state and reports the field and key instead of
/// publishing it; explicit snapshot reads and replies fail. When <paramref name="PublishesChanges"/>
/// is false (a ViewModel with validation), the collection only publishes full states, whose keys
/// are still checked.
/// </remarks>
public sealed record BridgeCollectionDescriptor<T>(
    string Name,
    Func<T, object?> Get,
    Action<Utf8JsonWriter, object?> WriteItem,
    Func<object?, string> Key,
    bool PublishesChanges = true);

internal sealed record BridgeCollectionChange<T>(
    BridgeCollectionDescriptor<T> Descriptor,
    string Kind,
    int Index,
    int OldIndex,
    string[] Keys,
    string[] Items);
