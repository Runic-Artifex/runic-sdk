using System;

namespace WebUIToolkit.CommandLine;

/// <summary>
/// Identifies the semantic category of a command-line outcome independently of
/// the numeric exit code selected by a host.
/// </summary>
public enum CommandExitCategory
{
    /// <summary>The command completed successfully.</summary>
    Success = 0,

    /// <summary>The invocation could not be parsed or its usage was invalid.</summary>
    Usage = 1,

    /// <summary>The parsed command or its options failed validation.</summary>
    Validation = 2,

    /// <summary>The invocation was cancelled.</summary>
    Cancelled = 3,

    /// <summary>A required command resource or service was unavailable.</summary>
    Unavailable = 4,

    /// <summary>The command reported an expected failure.</summary>
    CommandFailure = 5,

    /// <summary>The host or command infrastructure failed unexpectedly.</summary>
    HostFailure = 6,
}

/// <summary>
/// Defines the default process exit codes for command outcome categories.
/// </summary>
public static class CommandExitCodes
{
    /// <summary>The default success exit code.</summary>
    public const int Success = 0;

    /// <summary>The default usage or parse failure exit code.</summary>
    public const int Usage = 2;

    /// <summary>The default validation failure exit code.</summary>
    public const int Validation = 3;

    /// <summary>The default cancellation exit code.</summary>
    public const int Cancelled = 4;

    /// <summary>The default unavailable-resource exit code.</summary>
    public const int Unavailable = 5;

    /// <summary>The default expected command failure exit code.</summary>
    public const int CommandFailure = 10;

    /// <summary>The default unexpected host or software failure exit code.</summary>
    public const int HostFailure = 70;

    /// <summary>
    /// Gets the default numeric exit code for a semantic category.
    /// </summary>
    /// <param name="category">The semantic outcome category.</param>
    /// <returns>The corresponding default exit code.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> is not a defined category.
    /// </exception>
    public static int GetDefault(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Success => Success,
        CommandExitCategory.Usage => Usage,
        CommandExitCategory.Validation => Validation,
        CommandExitCategory.Cancelled => Cancelled,
        CommandExitCategory.Unavailable => Unavailable,
        CommandExitCategory.CommandFailure => CommandFailure,
        CommandExitCategory.HostFailure => HostFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}

/// <summary>
/// Maps semantic command outcome categories to process exit codes.
/// </summary>
/// <remarks>
/// Implementations may remap failures but must return zero only for
/// <see cref="CommandExitCategory.Success"/>.
/// </remarks>
public interface IExitCodePolicy
{
    /// <summary>
    /// Gets a process exit code for the supplied semantic category.
    /// </summary>
    /// <param name="category">The semantic outcome category.</param>
    /// <returns>A process exit code. Zero is reserved for success.</returns>
    int GetExitCode(CommandExitCategory category);
}

/// <summary>
/// Maps each semantic category to the default WebUIToolkit process exit code.
/// </summary>
public sealed class DefaultExitCodePolicy : IExitCodePolicy
{
    /// <summary>Gets the shared stateless default policy.</summary>
    public static DefaultExitCodePolicy Instance { get; } = new();

    private DefaultExitCodePolicy()
    {
    }

    /// <inheritdoc />
    public int GetExitCode(CommandExitCategory category) => CommandExitCodes.GetDefault(category);
}
