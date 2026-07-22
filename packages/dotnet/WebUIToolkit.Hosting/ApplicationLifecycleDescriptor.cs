using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace WebUIToolkit.Hosting;

/// <summary>Contains the immutable collaborators used by one application lifecycle.</summary>
public sealed class ApplicationLifecycleDescriptor
{
    private readonly ApplicationTimeoutOptions _timeouts;

    /// <summary>Initializes a descriptor and takes immutable snapshots of each registration sequence.</summary>
    public ApplicationLifecycleDescriptor(
        IApplicationHost host,
        IEnumerable<IApplicationValidator> validators,
        IEnumerable<IApplicationStartupParticipant> participants,
        IEnumerable<IApplicationModeRunner> modeRunners,
        IExitCodePolicy? exitCodePolicy = null,
        ApplicationTimeoutOptions? timeouts = null)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Validators = Snapshot(validators, nameof(validators));
        Participants = Snapshot(participants, nameof(participants));
        ModeRunners = Snapshot(modeRunners, nameof(modeRunners));
        ExitCodePolicy = exitCodePolicy ?? new DefaultExitCodePolicy();
        ApplicationTimeoutOptions sourceTimeouts = timeouts ?? new ApplicationTimeoutOptions();
        _timeouts = new ApplicationTimeoutOptions
        {
            StartupTimeout = sourceTimeouts.StartupTimeout,
            ParticipantStopTimeout = sourceTimeouts.ParticipantStopTimeout,
            SessionCloseTimeout = sourceTimeouts.SessionCloseTimeout,
            WindowCloseTimeout = sourceTimeouts.WindowCloseTimeout,
            HostStopTimeout = sourceTimeouts.HostStopTimeout,
            TotalShutdownTimeout = sourceTimeouts.TotalShutdownTimeout,
        };

        ValidateBoundedTimeout(_timeouts.StartupTimeout, nameof(ApplicationTimeoutOptions.StartupTimeout));
        ValidateTimeout(_timeouts.ParticipantStopTimeout, nameof(ApplicationTimeoutOptions.ParticipantStopTimeout));
        ValidateTimeout(_timeouts.SessionCloseTimeout, nameof(ApplicationTimeoutOptions.SessionCloseTimeout));
        ValidateTimeout(_timeouts.WindowCloseTimeout, nameof(ApplicationTimeoutOptions.WindowCloseTimeout));
        ValidateTimeout(_timeouts.HostStopTimeout, nameof(ApplicationTimeoutOptions.HostStopTimeout));
        ValidateBoundedTimeout(_timeouts.TotalShutdownTimeout, nameof(ApplicationTimeoutOptions.TotalShutdownTimeout));
    }

    /// <summary>Gets the neutral host lifetime bridge.</summary>
    public IApplicationHost Host { get; }

    /// <summary>Gets validators in registration order.</summary>
    public IReadOnlyList<IApplicationValidator> Validators { get; }

    /// <summary>Gets startup participants in registration order.</summary>
    public IReadOnlyList<IApplicationStartupParticipant> Participants { get; }

    /// <summary>Gets mode runners in registration order.</summary>
    public IReadOnlyList<IApplicationModeRunner> ModeRunners { get; }

    /// <summary>Gets the failure-to-exit-code policy.</summary>
    public IExitCodePolicy ExitCodePolicy { get; }

    /// <summary>Gets the bounded-operation timeout options.</summary>
    public ApplicationTimeoutOptions Timeouts => new()
    {
        StartupTimeout = _timeouts.StartupTimeout,
        ParticipantStopTimeout = _timeouts.ParticipantStopTimeout,
        SessionCloseTimeout = _timeouts.SessionCloseTimeout,
        WindowCloseTimeout = _timeouts.WindowCloseTimeout,
        HostStopTimeout = _timeouts.HostStopTimeout,
        TotalShutdownTimeout = _timeouts.TotalShutdownTimeout,
    };

    internal ApplicationTimeoutOptions TimeoutSnapshot => _timeouts;

    private static ReadOnlyCollection<T> Snapshot<T>(IEnumerable<T> values, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        T[] snapshot = values.ToArray();
        if (Array.Exists(snapshot, static value => value is null))
        {
            throw new ArgumentException("Registration sequences cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }

    private static void ValidateTimeout(TimeSpan timeout, string propertyName)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(propertyName, timeout, "Timeouts must be positive or infinite.");
        }
    }

    private static void ValidateBoundedTimeout(TimeSpan timeout, string propertyName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(propertyName, timeout, "The aggregate timeout must be finite and positive.");
        }
    }
}
