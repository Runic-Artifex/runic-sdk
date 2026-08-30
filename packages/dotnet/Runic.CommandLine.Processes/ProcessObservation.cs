using System;

namespace Runic.CommandLine.Processes;

/// <summary>Receives metadata-only lifecycle notifications without arguments, paths, or output.</summary>
public interface IProcessObserver
{
    /// <summary>Called after a child starts.</summary>
    /// <param name="startedAt">The UTC start timestamp.</param>
    void OnStarted(DateTimeOffset startedAt);

    /// <summary>Called after a request reaches a terminal state.</summary>
    /// <param name="state">The terminal state.</param>
    /// <param name="duration">The elapsed wall-clock duration.</param>
    void OnCompleted(ProcessState state, TimeSpan duration);
}
