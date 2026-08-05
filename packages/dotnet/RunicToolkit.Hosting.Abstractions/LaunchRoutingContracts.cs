using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace RunicToolkit.Hosting;

/// <summary>Describes a deterministic mode-runner cardinality failure.</summary>
public sealed class ApplicationModeRouteError
{
    private readonly ReadOnlyCollection<int> _matchingRegistrationIndexes;

    /// <summary>Initializes a mode-route error from registration-order indexes.</summary>
    /// <param name="kind">The launch kind that could not be routed uniquely.</param>
    /// <param name="matchingRegistrationIndexes">
    /// The zero-based registration indexes of runners matching <paramref name="kind"/>.
    /// </param>
    public ApplicationModeRouteError(
        LaunchKind kind,
        IReadOnlyList<int> matchingRegistrationIndexes)
    {
        ArgumentNullException.ThrowIfNull(matchingRegistrationIndexes);

        int[] snapshot = new int[matchingRegistrationIndexes.Count];
        for (int index = 0; index < matchingRegistrationIndexes.Count; index++)
        {
            int registrationIndex = matchingRegistrationIndexes[index];
            if (registrationIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(matchingRegistrationIndexes),
                    "Registration indexes cannot be negative.");
            }

            if (index > 0 && registrationIndex <= snapshot[index - 1])
            {
                throw new ArgumentException(
                    "Registration indexes must be strictly increasing.",
                    nameof(matchingRegistrationIndexes));
            }

            snapshot[index] = registrationIndex;
        }

        Kind = kind;
        _matchingRegistrationIndexes = Array.AsReadOnly(snapshot);
        Code = ApplicationFailureCodes.RunnerSelection;
        SafeMessage = "The selected launch kind must have exactly one mode runner.";
    }

    /// <summary>Gets the launch kind that did not have exactly one registered runner.</summary>
    public LaunchKind Kind { get; }

    /// <summary>Gets the number of runners registered for the selected kind.</summary>
    public int MatchCount => _matchingRegistrationIndexes.Count;

    /// <summary>Gets matching zero-based indexes in deterministic registration order.</summary>
    public IReadOnlyList<int> MatchingRegistrationIndexes => _matchingRegistrationIndexes;

    /// <summary>Gets the stable Hosting diagnostic code for runner selection.</summary>
    public string Code { get; }

    /// <summary>Gets a safe diagnostic that does not include consumer-provided data.</summary>
    public string SafeMessage { get; }
}

/// <summary>Contains either one selected runner or deterministic route error data.</summary>
public sealed class ApplicationModeRouteSelection
{
    private ApplicationModeRouteSelection(
        IApplicationModeRunner? runner,
        ApplicationModeRouteError? error)
    {
        Runner = runner;
        Error = error;
    }

    /// <summary>Gets the unique selected runner, when routing succeeded.</summary>
    public IApplicationModeRunner? Runner { get; }

    /// <summary>Gets deterministic cardinality error data, when routing failed.</summary>
    public ApplicationModeRouteError? Error { get; }

    /// <summary>Gets whether exactly one runner was selected.</summary>
    public bool IsSuccess => Runner is not null;

    /// <summary>Creates a successful route selection.</summary>
    /// <param name="runner">The unique runner selected for a launch kind.</param>
    /// <returns>A successful immutable selection.</returns>
    public static ApplicationModeRouteSelection Selected(IApplicationModeRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return new ApplicationModeRouteSelection(runner, null);
    }

    /// <summary>Creates a failed route selection.</summary>
    /// <param name="error">The deterministic runner-cardinality error.</param>
    /// <returns>A failed immutable selection.</returns>
    public static ApplicationModeRouteSelection Failed(ApplicationModeRouteError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ApplicationModeRouteSelection(null, error);
    }
}

/// <summary>Selects one mode runner from an immutable registration snapshot.</summary>
public interface IApplicationModeRouteTable
{
    /// <summary>Selects exactly one runner for <paramref name="kind"/>.</summary>
    /// <param name="kind">The already-classified launch kind.</param>
    /// <returns>A successful selection or stable cardinality error data.</returns>
    ApplicationModeRouteSelection SelectRunner(LaunchKind kind);
}
