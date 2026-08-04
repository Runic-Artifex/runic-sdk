namespace RunicCommandLine.Processes;

/// <summary>Describes the terminal state of a process request.</summary>
public enum ProcessState
{
    /// <summary>The child exited, regardless of its exit code.</summary>
    Exited = 0,

    /// <summary>The executable could not be started.</summary>
    StartFailed = 1,

    /// <summary>The executable policy rejected the request before start.</summary>
    Rejected = 2,

    /// <summary>The configured execution timeout elapsed.</summary>
    TimedOut = 3,

    /// <summary>The caller cancelled the request.</summary>
    Cancelled = 4,

    /// <summary>The process started, but its lifecycle could not be observed safely.</summary>
    ExecutionFailed = 5,
}

/// <summary>Classifies a sanitized process-start failure.</summary>
public enum ProcessStartFailureCategory
{
    /// <summary>The executable or working directory was not found.</summary>
    NotFound = 0,

    /// <summary>The operating system denied access.</summary>
    AccessDenied = 1,

    /// <summary>The request was not accepted by the process API.</summary>
    InvalidRequest = 2,

    /// <summary>The executable format or platform is unsupported.</summary>
    Unsupported = 3,

    /// <summary>The start failed for another sanitized reason.</summary>
    Other = 4,
}
