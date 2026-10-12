namespace Runic.Navigation;

/// <summary>How a navigation request ended, as reported by <see cref="NavigationOutcome.Kind"/>.</summary>
public enum NavigationOutcomeKind
{
    /// <summary>The transition committed.</summary>
    Committed,

    /// <summary>The transition was rejected; <see cref="NavigationOutcome.Rejection"/> says why.</summary>
    Rejected,

    /// <summary>A later request replaced this one before it committed.</summary>
    Superseded,

    /// <summary>The transition failed. Only the full navigation result carries the exception.</summary>
    Failed,
}

/// <summary>
/// A summary of a <see cref="NavigationResult{TContent}"/> without its .NET content and exception, so a ViewModel
/// can return it to a Runic View, for example as the output of a back command.
/// </summary>
/// <param name="Kind">How the request ended.</param>
/// <param name="Rejection">Why the request was rejected, for <see cref="NavigationOutcomeKind.Rejected"/>; otherwise <see langword="null"/>.</param>
public sealed record NavigationOutcome(NavigationOutcomeKind Kind, NavigationRejection? Rejection = null);
