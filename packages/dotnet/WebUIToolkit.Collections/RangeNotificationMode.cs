namespace WebUIToolkit.Collections;

/// <summary>
/// Selects how multi-item range mutations are reported to collection observers.
/// </summary>
public enum RangeNotificationMode
{
    /// <summary>
    /// Reports a compatible multi-item Add, Remove, Replace, or Move notification when possible.
    /// </summary>
    Range = 0,

    /// <summary>
    /// Reports a single Reset notification for each multi-item range mutation.
    /// </summary>
    Reset = 1,
}
