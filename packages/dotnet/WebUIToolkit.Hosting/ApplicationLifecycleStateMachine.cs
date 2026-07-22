using System;
using System.Threading;

namespace WebUIToolkit.Hosting;

/// <summary>Serializes and enforces the legal application lifecycle transition graph.</summary>
public sealed class ApplicationLifecycleStateMachine
{
    private readonly object _sync = new();
    private int _state = (int)ApplicationState.Created;

    /// <summary>Gets the current state.</summary>
    public ApplicationState State => (ApplicationState)Volatile.Read(ref _state);

    /// <summary>Attempts one transition without changing state when the edge is illegal.</summary>
    /// <param name="next">The requested next state.</param>
    /// <returns><see langword="true"/> when the transition was applied.</returns>
    public bool TryTransition(ApplicationState next)
    {
        lock (_sync)
        {
            ApplicationState current = (ApplicationState)_state;
            if (!IsLegalTransition(current, next))
            {
                return false;
            }

            Volatile.Write(ref _state, (int)next);
            return true;
        }
    }

    /// <summary>Applies one legal transition.</summary>
    /// <param name="next">The requested next state.</param>
    /// <exception cref="InvalidOperationException">The edge is not part of the lifecycle graph.</exception>
    public void Transition(ApplicationState next)
    {
        ApplicationState current = State;
        if (!TryTransition(next))
        {
            throw new InvalidOperationException($"Illegal lifecycle transition: {current} -> {next}.");
        }
    }

    private static bool IsLegalTransition(ApplicationState current, ApplicationState next)
    {
        return (current, next) switch
        {
            (ApplicationState.Created, ApplicationState.Validating) => true,
            (ApplicationState.Created, ApplicationState.Stopping) => true,
            (ApplicationState.Validating, ApplicationState.Starting) => true,
            (ApplicationState.Validating, ApplicationState.Stopping) => true,
            (ApplicationState.Starting, ApplicationState.Running) => true,
            (ApplicationState.Starting, ApplicationState.Stopping) => true,
            (ApplicationState.Running, ApplicationState.Stopping) => true,
            (ApplicationState.Stopping, ApplicationState.Stopped) => true,
            (ApplicationState.Stopping, ApplicationState.Faulted) => true,
            (ApplicationState.Stopped, ApplicationState.Disposed) => true,
            (ApplicationState.Faulted, ApplicationState.Disposed) => true,
            _ => false,
        };
    }
}
