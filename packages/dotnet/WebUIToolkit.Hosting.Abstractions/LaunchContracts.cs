using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

/// <summary>Identifies the single application mode selected for a launch.</summary>
public enum LaunchKind
{
    /// <summary>Run the interactive user interface.</summary>
    UserInterface,
    /// <summary>Run a command.</summary>
    Command,
    /// <summary>Render help.</summary>
    Help,
    /// <summary>Render version information.</summary>
    Version,
    /// <summary>The arguments do not describe a valid launch.</summary>
    Invalid,
}

/// <summary>Contains the side-effect-free result of launch classification.</summary>
public sealed record LaunchDecision
{
    /// <summary>Initializes a launch decision.</summary>
    public LaunchDecision(
        LaunchKind kind,
        IReadOnlyList<string> arguments,
        string? commandName = null,
        string? diagnostic = null)
    {
        Kind = kind;
        ArgumentNullException.ThrowIfNull(arguments);
        var snapshot = new string[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            snapshot[index] = arguments[index]
                ?? throw new ArgumentException("Launch arguments cannot contain null entries.", nameof(arguments));
        }

        Arguments = Array.AsReadOnly(snapshot);
        CommandName = commandName;
        Diagnostic = diagnostic;
    }

    /// <summary>Gets the selected launch kind.</summary>
    public LaunchKind Kind { get; }

    /// <summary>Gets an immutable snapshot of the classified arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the stable command name, when a command was selected.</summary>
    public string? CommandName { get; }

    /// <summary>Gets a safe classification diagnostic, when available.</summary>
    public string? Diagnostic { get; }
}

/// <summary>Classifies raw arguments without resolving application services.</summary>
public interface ILaunchIntentResolver
{
    /// <summary>Resolves exactly one launch decision.</summary>
    LaunchDecision Resolve(IReadOnlyList<string> arguments);
}

/// <summary>Executes one selected application mode.</summary>
public interface IApplicationModeRunner
{
    /// <summary>Gets the launch kind handled by this runner.</summary>
    LaunchKind Kind { get; }

    /// <summary>Executes the selected mode.</summary>
    Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken);
}
