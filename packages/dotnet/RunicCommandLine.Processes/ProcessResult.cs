using System;

namespace RunicCommandLine.Processes;

/// <summary>Contains a bounded, sanitized process execution result.</summary>
public sealed class ProcessResult
{
    internal ProcessResult(
        ProcessState state,
        int? exitCode,
        ProcessOutput standardOutput,
        ProcessOutput standardError,
        ProcessStartFailureCategory? startFailureCategory,
        CommandFault? fault,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        State = state;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        StartFailureCategory = startFailureCategory;
        Fault = fault;
        StartedAt = startedAt;
        EndedAt = endedAt;
    }

    /// <summary>Gets the terminal state.</summary>
    public ProcessState State { get; }

    /// <summary>Gets the child exit code when the child exited normally.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets captured standard output and its bounds metadata.</summary>
    public ProcessOutput StandardOutput { get; }

    /// <summary>Gets captured standard error and its bounds metadata.</summary>
    public ProcessOutput StandardError { get; }

    /// <summary>Gets a sanitized process-start failure category.</summary>
    public ProcessStartFailureCategory? StartFailureCategory { get; }

    /// <summary>Gets a stable consumer-safe fault for rejection or start failure.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Gets the UTC timestamp when request execution began.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets the UTC completion timestamp.</summary>
    public DateTimeOffset EndedAt { get; }

    /// <summary>Gets the elapsed wall-clock duration.</summary>
    public TimeSpan Duration => EndedAt - StartedAt;
}

/// <summary>Contains one decoded, bounded process output channel.</summary>
public sealed class ProcessOutput
{
    internal ProcessOutput(string text, long observedByteCount, bool isTruncated)
    {
        Text = text;
        ObservedByteCount = observedByteCount;
        IsTruncated = isTruncated;
    }

    /// <summary>Gets retained output decoded with the request encoding.</summary>
    public string Text { get; }

    /// <summary>Gets the total number of bytes observed while draining the channel.</summary>
    public long ObservedByteCount { get; }

    /// <summary>Gets a value indicating whether observed bytes exceeded the retained-byte cap.</summary>
    public bool IsTruncated { get; }
}
