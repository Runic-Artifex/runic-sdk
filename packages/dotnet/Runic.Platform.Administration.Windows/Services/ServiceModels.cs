using System.Collections.Immutable;

namespace Runic.Platform.Administration.Windows.Services;

/// <summary>Native service lifecycle state.</summary>
public enum ServiceState
{
    /// <summary>Stopped.</summary>
    Stopped = 1,
    /// <summary>Starting.</summary>
    StartPending = 2,
    /// <summary>Stopping.</summary>
    StopPending = 3,
    /// <summary>Running.</summary>
    Running = 4,
    /// <summary>Continuing.</summary>
    ContinuePending = 5,
    /// <summary>Pausing.</summary>
    PausePending = 6,
    /// <summary>Paused.</summary>
    Paused = 7
}

/// <summary>Start configuration of a Windows service.</summary>
public enum ServiceStartMode
{
    /// <summary>Boot-start driver.</summary>
    Boot = 0,
    /// <summary>System-start driver.</summary>
    System = 1,
    /// <summary>Automatic service.</summary>
    Automatic = 2,
    /// <summary>Demand-start service.</summary>
    Manual = 3,
    /// <summary>Disabled service.</summary>
    Disabled = 4
}

/// <summary>Recovery action after service failure.</summary>
public enum ServiceFailureActionKind
{
    /// <summary>No action.</summary>
    None = 0,
    /// <summary>Restart the service.</summary>
    Restart = 1,
    /// <summary>Reboot the computer (requires shutdown privilege).</summary>
    Reboot = 2,
    /// <summary>Run the configured recovery command.</summary>
    RunCommand = 3
}

/// <summary>A failure action and its delay.</summary>
public sealed record ServiceFailureAction(ServiceFailureActionKind Kind, TimeSpan Delay);

/// <summary>Failure recovery configuration. Null reset period means never reset.</summary>
public sealed record ServiceFailurePolicy(TimeSpan? ResetPeriod, ImmutableArray<ServiceFailureAction> Actions,
    string RebootMessage = "", string Command = "", bool OnNonCrashFailures = false);

/// <summary>Service state observed at a moment in time.</summary>
public sealed record ServiceStatus(ServiceState State, uint ProcessId, uint AcceptedControls,
    uint Win32ExitCode, uint ServiceSpecificExitCode, uint CheckPoint, TimeSpan WaitHint);

/// <summary>Service enumeration row; detailed configuration is retrieved separately.</summary>
public sealed record ServiceSummary(string Name, string DisplayName, ServiceStatus Status);

/// <summary>Configuration and status read from Windows. BinaryCommandLine includes arguments.</summary>
public sealed record ServiceSnapshot(string Name, string DisplayName, string BinaryCommandLine,
    string AccountName, ImmutableArray<string> Dependencies, ServiceStartMode StartMode,
    uint NativeServiceType, uint NativeErrorControl, string LoadOrderGroup, string Description,
    bool DelayedAutomaticStart, ServiceFailurePolicy FailurePolicy, ServiceStatus Status);

/// <summary>Creates a Win32 own-process service. Credentials are passed separately and are never part of this model.</summary>
public sealed record ServiceSpecification(string Name, string BinaryCommandLine)
{
    /// <summary>Display name; null uses Name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Start mode; driver modes are not supported for new own-process services.</summary>
    public ServiceStartMode StartMode { get; init; } = ServiceStartMode.Manual;
    /// <summary>Service identity; null selects LocalSystem.</summary>
    public string? AccountName { get; init; }
    /// <summary>Dependency names, including '+' prefixed load-order groups where needed.</summary>
    public ImmutableArray<string> Dependencies { get; init; } = [];
    /// <summary>Service description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Delayed automatic start, valid only for Automatic.</summary>
    public bool DelayedAutomaticStart { get; init; }
    /// <summary>Optional recovery configuration.</summary>
    public ServiceFailurePolicy? FailurePolicy { get; init; }
}

/// <summary>Selected service edits. Null leaves a field unchanged. Multi-field native changes are not transactional.</summary>
public sealed record ServiceUpdate
{
    /// <summary>Replacement executable command line.</summary>
    public string? BinaryCommandLine { get; init; }
    /// <summary>Replacement display name.</summary>
    public string? DisplayName { get; init; }
    /// <summary>Replacement service identity.</summary>
    public string? AccountName { get; init; }
    /// <summary>Replacement start mode.</summary>
    public ServiceStartMode? StartMode { get; init; }
    /// <summary>Replacement dependencies; an empty array clears them.</summary>
    public ImmutableArray<string>? Dependencies { get; init; }
    /// <summary>Replacement description; empty clears it.</summary>
    public string? Description { get; init; }
    /// <summary>Replacement delayed-start flag.</summary>
    public bool? DelayedAutomaticStart { get; init; }
    /// <summary>Replacement failure configuration.</summary>
    public ServiceFailurePolicy? FailurePolicy { get; init; }
}
