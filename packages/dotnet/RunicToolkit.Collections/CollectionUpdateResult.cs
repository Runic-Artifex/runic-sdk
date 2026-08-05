namespace RunicToolkit.Collections;

/// <summary>
/// Describes the edit plan applied by a successful collection reconciliation.
/// </summary>
/// <param name="Added">The number of inserted items.</param>
/// <param name="Removed">The number of removed items.</param>
/// <param name="Moved">The number of single-item moves.</param>
/// <param name="Replaced">The number of matched items replaced by resolver output.</param>
/// <param name="NotificationCount">The number of collection-change notifications emitted.</param>
/// <param name="UsedReset"><see langword="true"/> when one reset notification was used instead of granular notifications.</param>
public readonly record struct CollectionUpdateResult(
    int Added,
    int Removed,
    int Moved,
    int Replaced,
    int NotificationCount,
    bool UsedReset)
{
    /// <summary>
    /// Gets whether the reconciliation changed collection membership, order, or a matched item instance.
    /// </summary>
    public bool Changed => Added != 0 || Removed != 0 || Moved != 0 || Replaced != 0;
}
