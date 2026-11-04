namespace Runic.Application.Views;

/// <summary>
/// What a page changed in a row, for a <see cref="RunicRowEditorAttribute"/> method that takes it as its third
/// parameter: <c>void UpdateLine(int id, InvoiceLine line, RunicRowEdit edit)</c>.
/// </summary>
public sealed class RunicRowEdit
{
    internal RunicRowEdit(IReadOnlyList<string> path, object previous, string? connectionKey)
    {
        Path = path;
        Previous = previous;
        ConnectionKey = connectionKey;
    }

    /// <summary>The changed cell, as wire member names below the row, such as <c>["quantity"]</c> or <c>["product", "code"]</c>.</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>The row before this edit: the row that the updated row was copied from.</summary>
    public object Previous { get; }

    /// <summary>The key of the page's Bridge connection that sent the edit, when the transport names one.</summary>
    public string? ConnectionKey { get; }
}

/// <summary>When a cell write of an editable row is a conflict (<see cref="RunicRowEditorAttribute.Conflicts"/>).</summary>
public enum RunicRowConflicts
{
    /// <summary>When another writer changed the same cell after the state the page based its write on (the default).</summary>
    Cell,

    /// <summary>
    /// When anything in the row changed after that state, such as another user changing a line's product while this
    /// one edits its price.
    /// </summary>
    Row,
}

/// <summary>
/// Leaves a member of a row or of an object member without a field of its own (<c>field-paths/1</c>): the page cannot
/// write it alone, and the generated <c>{View}Fields</c> has no descriptor for it or for the members below it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicNoFieldAttribute : Attribute;
