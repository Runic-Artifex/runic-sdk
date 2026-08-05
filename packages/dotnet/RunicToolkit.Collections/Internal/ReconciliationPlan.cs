namespace RunicToolkit.Collections.Internal;

internal enum ReconciliationEditKind
{
    Add,
    Remove,
    Move,
    Replace,
}

internal readonly record struct ReconciliationEdit<T>(
    ReconciliationEditKind Kind,
    int Index,
    int OldIndex,
    T Item);

internal sealed class ReconciliationPlan<T>
{
    internal ReconciliationPlan(
        T[] desired,
        ReconciliationEdit<T>[] edits,
        int added,
        int removed,
        int moved,
        int replaced)
    {
        Desired = desired;
        Edits = edits;
        Added = added;
        Removed = removed;
        Moved = moved;
        Replaced = replaced;
    }

    internal T[] Desired { get; }

    internal ReconciliationEdit<T>[] Edits { get; }

    internal int Added { get; }

    internal int Removed { get; }

    internal int Moved { get; }

    internal int Replaced { get; }

    internal int EventCount => Edits.Length;
}
