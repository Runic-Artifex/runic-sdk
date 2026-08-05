namespace RunicToolkit.Collections;

/// <summary>
/// Configures the notification behavior of an <see cref="ObservableRangeCollection{T}"/>.
/// </summary>
public sealed record ObservableRangeCollectionOptions
{
    /// <summary>
    /// Gets the notification policy used by multi-item range mutations.
    /// </summary>
    public RangeNotificationMode RangeNotifications { get; init; } = RangeNotificationMode.Range;
}
