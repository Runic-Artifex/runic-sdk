using System.ComponentModel;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Views.Generated;

/// <summary>Generated, reflection-free metadata for a keyed collection.</summary>
/// <remarks>
/// The bridge checks the keys of every state and frame it writes. With a null row or a null, empty
/// or duplicate key it withholds the route's state and reports the field and key instead of
/// publishing it; explicit snapshot reads and replies fail. When <paramref name="publishesChanges"/>
/// is false (a ViewModel with validation), the collection only publishes full states, whose keys
/// are still checked.
/// </remarks>
/// <param name="name">The collection's wire name.</param>
/// <param name="get">Reads the collection from the ViewModel.</param>
/// <param name="writeItem">Writes one row.</param>
/// <param name="key">Reads the wire key of a row.</param>
/// <param name="publishesChanges">Whether the collection publishes indexed changes rather than only full states.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class BridgeCollectionDescriptor(
    string name,
    Func<object, object?> get,
    Action<Utf8JsonWriter, object?> writeItem,
    Func<object?, string> key,
    bool publishesChanges = true)
{
    /// <summary>The collection's wire name.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>Reads the collection from the ViewModel.</summary>
    public Func<object, object?> Get { get; } = get ?? throw new ArgumentNullException(nameof(get));

    /// <summary>Writes one row.</summary>
    public Action<Utf8JsonWriter, object?> WriteItem { get; } = writeItem ?? throw new ArgumentNullException(nameof(writeItem));

    /// <summary>Reads the wire key of a row.</summary>
    public Func<object?, string> Key { get; } = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>Whether the collection publishes indexed changes rather than only full states.</summary>
    public bool PublishesChanges { get; } = publishesChanges;

    /// <summary>
    /// The paths in a row that accept checked writes with <c>field-paths/1</c> (its cells), with the generated copy of a
    /// row with one cell changed; <see langword="null"/> when the rows are not editable.
    /// </summary>
    public IReadOnlyList<BridgeFieldPath>? FieldPaths { get; init; }

    /// <summary>
    /// Applies an updated row: calls the ViewModel's <see cref="RunicRowEditorAttribute"/> method with the row's key, the
    /// row and the edit. It throws to reject the update.
    /// </summary>
    public Action<object, object?, RunicRowEdit>? UpdateRow { get; init; }

    /// <summary>When a cell write is a conflict: per cell (the default) or per row.</summary>
    public RunicRowConflicts RowConflicts { get; init; }
}
