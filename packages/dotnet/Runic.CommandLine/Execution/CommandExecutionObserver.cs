using System;

namespace Runic.CommandLine;

/// <summary>Identifies safe execution lifecycle events.</summary>
public enum CommandExecutionEventKind
{
    /// <summary>A valid invocation scope was created.</summary>
    Started = 0,

    /// <summary>Typed binding completed.</summary>
    Bound = 1,

    /// <summary>The typed handler is about to execute.</summary>
    HandlerStarted = 2,

    /// <summary>The invocation and owned scope completed.</summary>
    Completed = 3,
}

/// <summary>Contains safe, value-free execution telemetry.</summary>
public sealed record CommandExecutionEvent(
    CommandExecutionEventKind Kind,
    CommandPath Path,
    string CorrelationId,
    CommandExitCategory? ExitCategory = null,
    int? ExitCode = null,
    string? FaultCode = null);

/// <summary>Observes safe execution lifecycle events without receiving token or payload values.</summary>
public interface ICommandExecutionObserver
{
    /// <summary>Observes one execution event.</summary>
    void Observe(CommandExecutionEvent executionEvent);
}
