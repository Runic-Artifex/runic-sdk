using System;

namespace RunicToolkit.Collections;

/// <summary>
/// Configures notification selection for collection reconciliation.
/// </summary>
public sealed record CollectionUpdateOptions
{
    /// <summary>
    /// Gets the notification policy. The default is <see cref="UpdateNotificationMode.Auto"/>.
    /// </summary>
    public UpdateNotificationMode Notifications { get; init; } = UpdateNotificationMode.Auto;

    /// <summary>
    /// Gets the greatest planned event count allowed by automatic granular notification selection.
    /// The default is 64. Automatic mode resets only when the planned count is strictly greater.
    /// </summary>
    public int MaxGranularEvents { get; init; } = 64;

    /// <summary>
    /// Gets the minimum size of the larger of the old and new sequences at which the change-ratio
    /// threshold is considered. The default is 64.
    /// </summary>
    public int ResetRatioMinimumCount { get; init; } = 64;

    /// <summary>
    /// Gets the greatest planned-event-to-sequence-size ratio allowed before automatic mode resets.
    /// The default is 0.35. Automatic mode resets only when the actual ratio is strictly greater.
    /// </summary>
    public double ResetChangeRatio { get; init; } = 0.35;

    internal void Validate()
    {
        if (!Enum.IsDefined(Notifications))
        {
            throw new ArgumentOutOfRangeException(nameof(Notifications), Notifications, "The notification mode is not defined.");
        }

        if (MaxGranularEvents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxGranularEvents), MaxGranularEvents, "The maximum granular event count cannot be negative.");
        }

        if (ResetRatioMinimumCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ResetRatioMinimumCount), ResetRatioMinimumCount, "The reset ratio minimum count cannot be negative.");
        }

        if (!double.IsFinite(ResetChangeRatio) || ResetChangeRatio < 0 || ResetChangeRatio > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ResetChangeRatio), ResetChangeRatio, "The reset change ratio must be finite and between zero and one, inclusive.");
        }
    }
}
