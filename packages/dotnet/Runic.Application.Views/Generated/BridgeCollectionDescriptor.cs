using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Views.Generated;

/// <summary>Generated, reflection-free metadata for a keyed collection.</summary>
/// <remarks>
/// The bridge checks the keys of every state and frame it writes. With a null row or a null, empty
/// or duplicate key it withholds the route's state and reports the field and key instead of
/// publishing it; explicit snapshot reads and replies fail. When <paramref name="PublishesChanges"/>
/// is false (a ViewModel with validation), the collection only publishes full states, whose keys
/// are still checked.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record BridgeCollectionDescriptor<T>(
    string Name,
    Func<T, object?> Get,
    Action<Utf8JsonWriter, object?> WriteItem,
    Func<object?, string> Key,
    bool PublishesChanges = true)
{
    /// <summary>
    /// The paths in a row that accept checked writes with <c>field-paths/1</c> (its cells), as wire member names, such as
    /// <c>["quantity"]</c>; <see langword="null"/> when the rows are not editable.
    /// </summary>
    public IReadOnlyList<string[]>? FieldPaths { get; init; }

    /// <summary>Decodes a row, for a cell write that rebuilt it. It throws like a setter's decoder for an invalid row.</summary>
    public Func<JsonElement, object?>? ReadRow { get; init; }

    /// <summary>
    /// Applies an updated row: calls the ViewModel's <see cref="RunicRowEditorAttribute"/> method with the row's key and
    /// the row. It throws to reject the update.
    /// </summary>
    public Action<T, object?>? UpdateRow { get; init; }
}
