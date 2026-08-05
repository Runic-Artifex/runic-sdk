using System;

namespace RunicToolkit.Hosting;

/// <summary>Classifies expected and unexpected application failures.</summary>
public enum ApplicationFailureCategory
{
    /// <summary>The launch input is invalid or ambiguous.</summary>
    Usage,
    /// <summary>Application configuration or validation is invalid.</summary>
    Configuration,
    /// <summary>The host or a startup participant failed.</summary>
    HostStartup,
    /// <summary>Frontend assets are missing or invalid.</summary>
    FrontendAssets,
    /// <summary>The external native runtime failed.</summary>
    NativeRuntime,
    /// <summary>Command execution failed.</summary>
    Command,
    /// <summary>User-interface execution failed.</summary>
    UserInterface,
    /// <summary>Shutdown failed when no earlier failure took precedence.</summary>
    Shutdown,
    /// <summary>External cancellation ended the lifecycle.</summary>
    Cancelled,
    /// <summary>An unexpected exception was not mapped by a more specific policy.</summary>
    Unhandled,
}

/// <summary>Contains a sanitized, stable application failure.</summary>
public sealed record ApplicationFailure(
    ApplicationFailureCategory Category,
    string Code,
    string SafeMessage,
    Exception? Exception = null,
    bool IsExpected = true);

/// <summary>Contains the primary terminal result selected by the lifecycle.</summary>
public sealed record ApplicationRunResult
{
    /// <summary>Initializes a run result.</summary>
    public ApplicationRunResult(int? exitCode, ApplicationFailure? failure = null)
    {
        if (exitCode is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(exitCode), "Exit codes cannot be negative.");
        }

        if (exitCode is null && failure is null)
        {
            throw new ArgumentException("A result must contain an exit code, a failure, or both.", nameof(exitCode));
        }

        ExitCode = exitCode;
        Failure = failure;
    }

    /// <summary>Gets the selected process exit code, if it has already been mapped.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the primary failure, when the lifecycle failed.</summary>
    public ApplicationFailure? Failure { get; }

    /// <summary>Gets whether this is a normal zero exit without a failure.</summary>
    public bool IsSuccess => ExitCode == 0 && Failure is null;

    /// <summary>Creates a completed result from an application or command exit code.</summary>
    public static ApplicationRunResult FromExitCode(int exitCode) => new(exitCode);

    /// <summary>Creates an unmapped failure result.</summary>
    public static ApplicationRunResult FromFailure(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new ApplicationRunResult(null, failure);
    }
}

/// <summary>Maps stable failures to process exit codes.</summary>
public interface IExitCodePolicy
{
    /// <summary>Gets the process exit code for a failure.</summary>
    int GetExitCode(ApplicationFailure failure);
}

/// <summary>Declares the exact Wave A Hosting diagnostic allocation.</summary>
public static class ApplicationFailureCodes
{
    /// <summary>Validation failed or rejected the launch.</summary>
    public const string Validation = "RTKHOST1001";
    /// <summary>The neutral application host failed to start.</summary>
    public const string HostStart = "RTKHOST1101";
    /// <summary>A startup participant failed.</summary>
    public const string ParticipantStart = "RTKHOST1102";
    /// <summary>The startup deadline expired.</summary>
    public const string StartupTimeout = "RTKHOST1103";
    /// <summary>The selected mode did not have exactly one runner.</summary>
    public const string RunnerSelection = "RTKHOST1201";
    /// <summary>The selected mode runner failed.</summary>
    public const string RunnerFailure = "RTKHOST1202";
    /// <summary>External cancellation won terminal-result selection.</summary>
    public const string Cancellation = "RTKHOST1301";
    /// <summary>A startup participant failed to stop.</summary>
    public const string ParticipantStop = "RTKHOST1401";
    /// <summary>A bounded teardown operation timed out.</summary>
    public const string StopTimeout = "RTKHOST1402";
    /// <summary>The neutral application host failed to stop.</summary>
    public const string HostStop = "RTKHOST1403";
    /// <summary>The neutral application host failed to dispose.</summary>
    public const string Dispose = "RTKHOST1404";
    /// <summary>The total shutdown deadline expired.</summary>
    public const string TotalShutdownTimeout = "RTKHOST1405";
}
