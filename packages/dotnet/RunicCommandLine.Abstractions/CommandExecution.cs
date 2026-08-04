using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RunicCommandLine;

/// <summary>Executes a command with typed options and a typed semantic result.</summary>
/// <typeparam name="TOptions">The immutable bound options type.</typeparam>
/// <typeparam name="TResult">The successful command result type.</typeparam>
public interface ICommandHandler<in TOptions, TResult>
{
    /// <summary>Executes one command invocation.</summary>
    /// <param name="options">The bound command options.</param>
    /// <param name="context">The invocation-local execution context.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The semantic command outcome.</returns>
    ValueTask<CommandOutcome<TResult>> ExecuteAsync(
        TOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>Provides invocation-local state to a typed command handler.</summary>
public sealed class CommandExecutionContext
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Initializes an invocation-local command execution context.</summary>
    /// <param name="services">The scoped service provider for this invocation.</param>
    /// <param name="console">The invocation-local console.</param>
    /// <param name="path">The canonical command path.</param>
    /// <param name="outputMode">The selected presentation mode.</param>
    /// <param name="culture">The culture selected for presentation.</param>
    /// <param name="correlationId">A non-empty opaque correlation identifier.</param>
    /// <exception cref="ArgumentNullException">A required reference is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="correlationId"/> is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outputMode"/> is not defined.</exception>
    public CommandExecutionContext(
        IServiceProvider services,
        ICommandConsole console,
        CommandPath path,
        CommandOutputMode outputMode,
        CultureInfo culture,
        string correlationId)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(culture);
        ValidateCorrelationId(correlationId);

        if (!Enum.IsDefined(outputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(outputMode));
        }

        Services = services;
        Console = console;
        Path = path;
        OutputMode = outputMode;
        Culture = CultureInfo.ReadOnly((CultureInfo)culture.Clone());
        CorrelationId = correlationId;
    }

    /// <summary>Gets the scoped service provider for this invocation.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Gets the invocation-local console.</summary>
    public ICommandConsole Console { get; }

    /// <summary>Gets the canonical command path.</summary>
    public CommandPath Path { get; }

    /// <summary>Gets the selected presentation mode.</summary>
    public CommandOutputMode OutputMode { get; }

    /// <summary>Gets the culture selected for presentation.</summary>
    public CultureInfo Culture { get; }

    /// <summary>Gets the opaque correlation identifier.</summary>
    public string CorrelationId { get; }

    private static void ValidateCorrelationId(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(correlationId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Correlation identifiers must contain valid Unicode text.",
                nameof(correlationId),
                exception);
        }

        if (byteCount > 128)
        {
            throw new ArgumentException(
                "Correlation identifiers cannot exceed 128 UTF-8 bytes.",
                nameof(correlationId));
        }

        foreach (char character in correlationId)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw new ArgumentException(
                    "Correlation identifiers cannot contain control or whitespace characters.",
                    nameof(correlationId));
            }
        }

        foreach (Rune rune in correlationId.EnumerateRunes())
        {
            int scalar = rune.Value;
            if (scalar is >= 0xFDD0 and <= 0xFDEF || (scalar & 0xFFFE) == 0xFFFE)
            {
                throw new ArgumentException(
                    "Correlation identifiers cannot contain Unicode noncharacters.",
                    nameof(correlationId));
            }
        }
    }
}

/// <summary>Owns the scoped services for one valid command invocation.</summary>
public interface ICommandExecutionScope : IAsyncDisposable
{
    /// <summary>Gets the invocation's scoped services.</summary>
    IServiceProvider Services { get; }
}

/// <summary>Creates isolated scopes for valid command invocations.</summary>
public interface ICommandExecutionScopeFactory
{
    /// <summary>Creates a new invocation scope.</summary>
    ICommandExecutionScope CreateScope();
}

/// <summary>
/// Creates a handler through an explicitly registered, closed generic factory.
/// </summary>
/// <typeparam name="THandler">The handler type.</typeparam>
/// <remarks>
/// Implementations must not discover handler types or constructors through
/// assembly scanning or runtime reflection.
/// </remarks>
public interface ICommandHandlerFactory<out THandler>
    where THandler : notnull
{
    /// <summary>Creates or resolves the handler from invocation-scoped services.</summary>
    /// <param name="services">The invocation-scoped service provider.</param>
    THandler Create(IServiceProvider services);
}
