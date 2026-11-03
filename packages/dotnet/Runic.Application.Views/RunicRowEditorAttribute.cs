namespace Runic.Application.Views;

/// <summary>
/// Makes the rows of a <see cref="RunicCollectionAttribute"/> collection editable from the page, cell by cell: the page
/// writes one cell, such as a line's quantity, and the Bridge calls this method with the row's key and the row with
/// that cell changed.
/// </summary>
/// <remarks>
/// <para>
/// The method takes the row's key and the updated row, such as <c>void UpdateLine(int id, InvoiceLine line)</c>, and
/// replaces the row in the collection (<c>Lines[index] = line</c>), recomputes what depends on it and may normalize it.
/// Throw to reject the change; the page keeps its draft and shows the exception's message. Rows are immutable values
/// here: the Bridge builds the updated row from the current one, so records, init-only properties and mutable classes
/// all work.
/// </para>
/// <para>
/// Each cell is a checked field (<c>field-paths/1</c>): a cell that another writer changed after the page read it is a
/// conflict, not overwritten. The generated <c>{View}Fields.lines.at(key).quantity</c> descriptors bind inputs to the
/// cells, with drafts, conflicts and the cells' validation messages.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RunicRowEditorAttribute : Attribute
{
    /// <summary>Makes the rows of the ViewModel's collection whose row type is the method's second parameter editable.</summary>
    public RunicRowEditorAttribute() { }

    /// <summary>Makes the rows of the named collection property editable.</summary>
    /// <param name="collection">The collection property, such as <c>nameof(Lines)</c>.</param>
    public RunicRowEditorAttribute(string collection) => Collection = collection;

    /// <summary>The collection property, or <see langword="null"/> to find it by the row type.</summary>
    public string? Collection { get; }
}
