using System.Collections.Immutable;

namespace Runic.Platform.Administration.Windows.Tasks;

/// <summary>Task Scheduler logon identity mode.</summary>
public enum TaskLogonType
{
    /// <summary>No logon identity.</summary>
    None = 0,
    /// <summary>Password supplied at registration.</summary>
    Password = 1,
    /// <summary>Service-for-user logon without stored password.</summary>
    ServiceForUser = 2,
    /// <summary>Existing interactive token.</summary>
    InteractiveToken = 3,
    /// <summary>Group activation.</summary>
    Group = 4,
    /// <summary>Built-in service account.</summary>
    ServiceAccount = 5,
    /// <summary>Legacy native XML logon mode, retained when inspecting or updating existing tasks.</summary>
    InteractiveTokenOrPassword = 6
}

/// <summary>Task Scheduler observed state.</summary>
public enum ScheduledTaskState
{
    /// <summary>Unknown.</summary>
    Unknown,
    /// <summary>Disabled.</summary>
    Disabled,
    /// <summary>Queued.</summary>
    Queued,
    /// <summary>Ready.</summary>
    Ready,
    /// <summary>Running.</summary>
    Running
}

/// <summary>Executable task action.</summary>
public sealed record TaskExecutableAction(string Path, string Arguments = "", string WorkingDirectory = "");

/// <summary>Task identity. Passwords are supplied only to registration operations.</summary>
public sealed record TaskPrincipal(string Identity, TaskLogonType LogonType, bool HighestPrivileges = false);

/// <summary>Common trigger settings.</summary>
/// <remarks>
/// <para>The set of trigger types is closed: only the ten trigger records in this namespace derive from it.</para>
/// <para>A boundary's <see cref="DateTime.Kind"/> selects how Task Scheduler interprets it:</para>
/// <list type="bullet">
/// <item><description><see cref="DateTimeKind.Unspecified"/>: a wall-clock time of the target machine, written without
/// an offset. This is Task Scheduler's default: the trigger follows that machine's time zone and daylight saving time.</description></item>
/// <item><description><see cref="DateTimeKind.Utc"/>: a point in time, written with <c>Z</c> (Task Scheduler's
/// "synchronize across time zones").</description></item>
/// <item><description><see cref="DateTimeKind.Local"/>: a point in time, written with the UTC offset that the computer
/// running this client has at that time.</description></item>
/// </list>
/// <para>Boundaries have whole seconds; a value with a fraction of a second is rejected with an <see cref="ArgumentException"/>.</para>
/// </remarks>
public abstract record TaskTrigger
{
    private protected TaskTrigger() { }
    /// <summary>Optional start boundary; its <see cref="DateTime.Kind"/> selects local wall-clock or synchronized time.</summary>
    public DateTime? StartBoundary { get; init; }
    /// <summary>Optional end boundary; its <see cref="DateTime.Kind"/> selects local wall-clock or synchronized time.</summary>
    public DateTime? EndBoundary { get; init; }
    /// <summary>Whether the trigger is enabled.</summary>
    public bool Enabled { get; init; } = true;
}
/// <summary>One-time trigger. A start boundary is required.</summary>
public sealed record TimeTaskTrigger : TaskTrigger;
/// <summary>Daily calendar trigger.</summary>
public sealed record DailyTaskTrigger(int DaysInterval = 1) : TaskTrigger;
/// <summary>Weekly calendar trigger.</summary>
public sealed record WeeklyTaskTrigger(ImmutableArray<DayOfWeek> Days, int WeeksInterval = 1) : TaskTrigger;
/// <summary>Monthly calendar trigger using explicit month numbers and days (1-31).</summary>
public sealed record MonthlyTaskTrigger(ImmutableArray<int> Months, ImmutableArray<int> Days, bool LastDay = false) : TaskTrigger;
/// <summary>Boot trigger.</summary>
public sealed record BootTaskTrigger(TimeSpan Delay = default) : TaskTrigger;
/// <summary>Logon trigger; null user applies to any user.</summary>
public sealed record LogonTaskTrigger(string? UserId = null, TimeSpan Delay = default) : TaskTrigger;
/// <summary>Idle trigger.</summary>
public sealed record IdleTaskTrigger : TaskTrigger;
/// <summary>Registration trigger.</summary>
public sealed record RegistrationTaskTrigger(TimeSpan Delay = default) : TaskTrigger;
/// <summary>Event trigger with a Windows event subscription query.</summary>
public sealed record EventTaskTrigger(string Subscription, TimeSpan Delay = default) : TaskTrigger;
/// <summary>Supported session changes.</summary>
public enum TaskSessionChange
{
    /// <summary>Console connection.</summary>
    ConsoleConnect,
    /// <summary>Console disconnection.</summary>
    ConsoleDisconnect,
    /// <summary>Remote connection.</summary>
    RemoteConnect,
    /// <summary>Remote disconnection.</summary>
    RemoteDisconnect,
    /// <summary>Session lock.</summary>
    SessionLock,
    /// <summary>Session unlock.</summary>
    SessionUnlock
}
/// <summary>Session-state trigger.</summary>
public sealed record SessionStateTaskTrigger(TaskSessionChange Change, string? UserId = null, TimeSpan Delay = default) : TaskTrigger;

/// <summary>Typed task creation settings. Native XML import supports additional settings and action kinds.</summary>
public sealed record ScheduledTaskSpecification(TaskPrincipal Principal, ImmutableArray<TaskExecutableAction> Actions, ImmutableArray<TaskTrigger> Triggers)
{
    /// <summary>Task description.</summary>
    public string Description { get; init; } = "";
    /// <summary>Initial enabled state.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Run after a missed start.</summary>
    public bool StartWhenAvailable { get; init; }
    /// <summary>Wake the computer for the task.</summary>
    public bool WakeToRun { get; init; }
    /// <summary>Hide the task from normal enumeration.</summary>
    public bool Hidden { get; init; }
    /// <summary>Execution limit; zero means unlimited.</summary>
    public TimeSpan ExecutionTimeLimit { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>Selected task edits. Null preserves the corresponding native XML subtree.</summary>
public sealed record ScheduledTaskUpdate
{
    /// <summary>Replacement description.</summary>
    public string? Description { get; init; }
    /// <summary>Replacement enabled state.</summary>
    public bool? Enabled { get; init; }
    /// <summary>Replacement executable actions; null preserves all native actions.</summary>
    public ImmutableArray<TaskExecutableAction>? Actions { get; init; }
    /// <summary>Replacement triggers; null preserves all native triggers.</summary>
    public ImmutableArray<TaskTrigger>? Triggers { get; init; }
    /// <summary>Replacement execution time limit.</summary>
    public TimeSpan? ExecutionTimeLimit { get; init; }
}

/// <summary>A registered task and its complete native XML, preserving unsupported settings.</summary>
/// <remarks>LastRunTime and NextRunTime are wall-clock times as Task Scheduler reports them, in the target machine's
/// local time with <see cref="DateTimeKind.Unspecified"/>. For a remote machine their offset is not known.</remarks>
public sealed record ScheduledTaskSnapshot(string Name, string Path, ScheduledTaskState State, bool Enabled,
    DateTime? LastRunTime, int LastTaskResult, DateTime? NextRunTime, string Xml);

/// <summary>A task instance started by the scheduler; registration is not proof of successful execution.</summary>
public sealed record RunningTaskSnapshot(string InstanceId, string Path, ScheduledTaskState State);
