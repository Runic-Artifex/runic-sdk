using System;
using System.Threading;
using System.Threading.Tasks;

namespace RunicCommandLine;

/// <summary>
/// Provides invocation-local console input and separated output channels.
/// </summary>
/// <remarks>
/// Implementations must not write through process-global <see cref="System.Console"/>
/// state when another invocation-local destination has been supplied.
/// </remarks>
public interface ICommandConsole
{
    /// <summary>
    /// Gets whether the console can safely accept an interactive prompt.
    /// </summary>
    bool IsInteractive { get; }

    /// <summary>Gets whether standard input is redirected.</summary>
    bool IsInputRedirected { get; }

    /// <summary>Gets whether standard output is redirected.</summary>
    bool IsOutputRedirected { get; }

    /// <summary>Gets whether standard error is redirected.</summary>
    bool IsErrorRedirected { get; }

    /// <summary>Reads one line from standard input.</summary>
    /// <param name="cancellationToken">Cancels the pending read.</param>
    /// <returns>The line without its terminator, or null at end of input.</returns>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>Writes characters to standard output without adding a terminator.</summary>
    /// <param name="value">The characters to write.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    ValueTask WriteOutAsync(
        ReadOnlyMemory<char> value,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes an exact byte frame to standard output without transcoding or
    /// adding a byte-order mark or terminator.
    /// </summary>
    /// <param name="value">The bytes to write.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    /// <remarks>
    /// The machine-output dispatcher uses this member to own the complete
    /// invocation-local standard-output frame. Console implementations must
    /// not retain <paramref name="value"/> after the returned task completes.
    /// </remarks>
    ValueTask WriteOutBytesAsync(
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken);

    /// <summary>Writes characters to standard error without adding a terminator.</summary>
    /// <param name="value">The characters to write.</param>
    /// <param name="cancellationToken">Cancels the pending write.</param>
    ValueTask WriteErrorAsync(
        ReadOnlyMemory<char> value,
        CancellationToken cancellationToken);
}
