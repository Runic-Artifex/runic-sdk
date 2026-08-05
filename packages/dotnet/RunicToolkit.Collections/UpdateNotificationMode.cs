namespace RunicToolkit.Collections;

/// <summary>
/// Selects how <see cref="ObservableRangeCollection{T}.UpdateTo(System.Collections.Generic.IEnumerable{T}, System.Collections.Generic.IEqualityComparer{T}?, System.Func{T, T, T}?, CollectionUpdateOptions?)"/>
/// reports a reconciliation.
/// </summary>
public enum UpdateNotificationMode
{
    /// <summary>
    /// Uses granular notifications until the configured event-count or change-ratio threshold is exceeded.
    /// </summary>
    Auto,

    /// <summary>
    /// Emits one coherent, single-item notification for every planned edit.
    /// </summary>
    Granular,

    /// <summary>
    /// Installs the final sequence and emits one reset notification.
    /// </summary>
    Reset,
}
