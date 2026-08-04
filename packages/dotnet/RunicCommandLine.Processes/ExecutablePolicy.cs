using System;
using System.Collections.Generic;
using System.IO;

namespace RunicCommandLine.Processes;

/// <summary>Authorizes a fully constructed process request before start.</summary>
public interface IExecutablePolicy
{
    /// <summary>Evaluates a request without starting a process.</summary>
    /// <param name="request">The immutable request.</param>
    /// <returns>A stable policy decision.</returns>
    ExecutablePolicyDecision Evaluate(ProcessRequest request);
}

/// <summary>Represents a stable executable-policy decision.</summary>
public sealed class ExecutablePolicyDecision
{
    private ExecutablePolicyDecision(bool isAllowed, CommandFault? fault)
    {
        IsAllowed = isAllowed;
        Fault = fault;
    }

    /// <summary>Gets a value indicating whether start is authorized.</summary>
    public bool IsAllowed { get; }

    /// <summary>Gets the sanitized rejection fault, when rejected.</summary>
    public CommandFault? Fault { get; }

    /// <summary>Creates an allowed decision.</summary>
    /// <returns>An allowed decision.</returns>
    public static ExecutablePolicyDecision Allow() => new(true, null);

    /// <summary>Creates a rejected decision.</summary>
    /// <param name="code">Stable policy fault code.</param>
    /// <param name="message">Safe presentation message.</param>
    /// <returns>A rejected decision.</returns>
    public static ExecutablePolicyDecision Reject(string code, string message) =>
        new(false, new CommandFault(code, message));
}

/// <summary>
/// Applies baseline local-execution validation and optional executable and working-directory roots.
/// </summary>
public sealed class LocalExecutablePolicy : IExecutablePolicy
{
    private readonly string[] executableRoots;
    private readonly string[] workingDirectoryRoots;

    /// <summary>Initializes a local executable policy.</summary>
    /// <param name="executableRoots">
    /// Optional absolute roots. When present, executable requests must use a rooted path beneath one of them.
    /// </param>
    /// <param name="workingDirectoryRoots">
    /// Optional absolute roots. When present, a supplied working directory must be beneath one of them.
    /// </param>
    public LocalExecutablePolicy(
        IEnumerable<string>? executableRoots = null,
        IEnumerable<string>? workingDirectoryRoots = null)
    {
        this.executableRoots = NormalizeRoots(executableRoots, nameof(executableRoots));
        this.workingDirectoryRoots = NormalizeRoots(workingDirectoryRoots, nameof(workingDirectoryRoots));
    }

    /// <inheritdoc />
    public ExecutablePolicyDecision Evaluate(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsPathPermitted(request.FileName, executableRoots, requireRootedWhenConfigured: true))
        {
            return ExecutablePolicyDecision.Reject(
                ProcessFaultCodes.ExecutableRejected,
                "The executable is not permitted by the configured policy.");
        }

        if (request.WorkingDirectory is not null &&
            !IsPathPermitted(request.WorkingDirectory, workingDirectoryRoots, requireRootedWhenConfigured: true))
        {
            return ExecutablePolicyDecision.Reject(
                ProcessFaultCodes.WorkingDirectoryRejected,
                "The working directory is not permitted by the configured policy.");
        }

        return ExecutablePolicyDecision.Allow();
    }

    private static string[] NormalizeRoots(IEnumerable<string>? roots, string parameterName)
    {
        if (roots is null)
        {
            return Array.Empty<string>();
        }

        var normalized = new List<string>();
        foreach (string root in roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root, parameterName);
            if (!Path.IsPathFullyQualified(root))
            {
                throw new ArgumentException("Policy roots must be fully qualified.", parameterName);
            }

            normalized.Add(EnsureTrailingSeparator(Path.GetFullPath(root)));
        }

        return normalized.ToArray();
    }

    private static bool IsPathPermitted(
        string value,
        string[] allowedRoots,
        bool requireRootedWhenConfigured)
    {
        if (allowedRoots.Length == 0)
        {
            return true;
        }

        if (requireRootedWhenConfigured && !Path.IsPathFullyQualified(value))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = EnsureTrailingSeparator(Path.GetFullPath(value));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        for (int index = 0; index < allowedRoots.Length; index++)
        {
            if (candidate.StartsWith(allowedRoots[index], comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : string.Concat(path, Path.DirectorySeparatorChar);
}
