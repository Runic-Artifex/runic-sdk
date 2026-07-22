using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WebUIToolkit.Hosting;

/// <summary>Selects mode runners from a deterministic immutable registration snapshot.</summary>
public sealed class ApplicationModeRouteTable : IApplicationModeRouteTable
{
    private readonly ReadOnlyCollection<RouteRegistration> _registrations;
    private readonly ReadOnlyCollection<IApplicationModeRunner> _runners;

    /// <summary>Initializes a route table and snapshots runners in registration order.</summary>
    /// <param name="runners">Mode runners in deterministic registration order.</param>
    public ApplicationModeRouteTable(IEnumerable<IApplicationModeRunner> runners)
    {
        ArgumentNullException.ThrowIfNull(runners);

        List<IApplicationModeRunner> snapshot = [];
        List<RouteRegistration> registrations = [];
        foreach (IApplicationModeRunner runner in runners)
        {
            IApplicationModeRunner registration = runner ?? throw new ArgumentException(
                "Mode-runner registrations cannot contain null entries.",
                nameof(runners));
            registrations.Add(new RouteRegistration(
                registrations.Count,
                registration.Kind,
                registration));
            snapshot.Add(registration);
        }

        _registrations = Array.AsReadOnly(registrations.ToArray());
        _runners = Array.AsReadOnly(snapshot.ToArray());
    }

    /// <summary>Gets the immutable runner snapshot in registration order.</summary>
    public IReadOnlyList<IApplicationModeRunner> Runners => _runners;

    /// <inheritdoc />
    public ApplicationModeRouteSelection SelectRunner(LaunchKind kind)
    {
        IApplicationModeRunner? selected = null;
        List<int>? matches = null;

        for (int index = 0; index < _registrations.Count; index++)
        {
            RouteRegistration registration = _registrations[index];
            if (registration.Kind != kind)
            {
                continue;
            }

            matches ??= [];
            matches.Add(registration.Index);
            selected ??= registration.Runner;
        }

        return matches is { Count: 1 }
            ? ApplicationModeRouteSelection.Selected(selected!)
            : ApplicationModeRouteSelection.Failed(
                new ApplicationModeRouteError(
                    kind,
                    matches is null ? Array.Empty<int>() : matches));
    }

    private sealed record RouteRegistration(
        int Index,
        LaunchKind Kind,
        IApplicationModeRunner Runner);
}
