using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.Hosting;

/// <summary>
/// Classifies one launch and executes it through the deterministic lifecycle kernel.
/// </summary>
public sealed class RunicToolkitApplication : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly ApplicationCompositionDescriptor _descriptor;
    private readonly ApplicationLifecycleKernel _lifecycle;
    private int _disposeRequested;
    private int _runStatus;

    /// <summary>Initializes an application from an immutable composition descriptor.</summary>
    public RunicToolkitApplication(ApplicationCompositionDescriptor descriptor)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _lifecycle = new ApplicationLifecycleKernel(
            descriptor.LifecycleDescriptor,
            descriptor.TimeProvider,
            descriptor.LifecycleEventSink);
    }

    /// <summary>Gets the frozen composition descriptor.</summary>
    public ApplicationCompositionDescriptor Descriptor => _descriptor;

    /// <summary>Gets the current serialized lifecycle state.</summary>
    public ApplicationState State => _lifecycle.State;

    /// <summary>Gets the stable completion shared by lifecycle stop callers.</summary>
    public Task<ApplicationRunResult> Completion => _lifecycle.Completion;

    /// <summary>Gets the controller that converges competing stop requests.</summary>
    public IApplicationStopController StopController => _lifecycle.StopController;

    /// <summary>Gets cleanup failures that did not replace an earlier non-success result.</summary>
    public IReadOnlyList<ApplicationFailure> SecondaryFailures => _lifecycle.SecondaryFailures;

    /// <summary>Classifies and runs a launch with no arguments.</summary>
    public Task<ApplicationRunResult> RunAsync(CancellationToken cancellationToken = default) =>
        RunAsync(Array.Empty<string>(), cancellationToken);

    /// <summary>
    /// Snapshots and classifies arguments before lifecycle validation or host startup, then runs
    /// the selected mode exactly once.
    /// </summary>
    public Task<ApplicationRunResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        string[] argumentSnapshot = SnapshotArguments(arguments);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeRequested) != 0,
            this);

        if (Interlocked.CompareExchange(ref _runStatus, 1, 0) != 0)
        {
            throw new InvalidOperationException("A built application can be run exactly once.");
        }

        lock (_sync)
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                Interlocked.Exchange(ref _runStatus, 2);
                ObjectDisposedException.ThrowIf(true, this);
            }

            LaunchDecision decision;
            try
            {
                decision = _descriptor.LaunchIntentResolver.Resolve(argumentSnapshot)
                    ?? throw new InvalidOperationException(
                        "The configured launch-intent resolver returned a null decision.");
            }
            catch
            {
                Interlocked.Exchange(
                    ref _runStatus,
                    Volatile.Read(ref _disposeRequested) == 0 ? 0 : 2);
                throw;
            }

            return _lifecycle.RunAsync(decision, cancellationToken);
        }
    }

    /// <summary>Requests stop when necessary and disposes the lifecycle exactly once.</summary>
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposeRequested, 1);
        lock (_sync)
        {
            Interlocked.Exchange(ref _runStatus, 2);
            return _lifecycle.DisposeAsync();
        }
    }

    private static string[] SnapshotArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] snapshot = new string[arguments.Count];
        for (int index = 0; index < arguments.Count; index++)
        {
            snapshot[index] = arguments[index]
                ?? throw new ArgumentException(
                    "Launch arguments cannot contain null entries.",
                    nameof(arguments));
        }

        return snapshot;
    }
}
