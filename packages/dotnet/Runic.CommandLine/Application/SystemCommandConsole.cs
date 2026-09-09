using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Connects an application to the process standard streams.</summary>
public sealed class SystemCommandConsole : ICommandConsole
{
    private readonly System.IO.TextReader _input = Console.In;
    private readonly System.IO.TextWriter _output = Console.Out;
    private readonly System.IO.TextWriter _error = Console.Error;
    /// <inheritdoc />
    public bool IsInteractive => !IsInputRedirected && !IsOutputRedirected;
    /// <inheritdoc />
    public bool IsInputRedirected => Console.IsInputRedirected;
    /// <inheritdoc />
    public bool IsOutputRedirected => Console.IsOutputRedirected;
    /// <inheritdoc />
    public bool IsErrorRedirected => Console.IsErrorRedirected;
    /// <inheritdoc />
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => _input.ReadLineAsync(cancellationToken);
    /// <inheritdoc />
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => new(_output.WriteAsync(value, cancellationToken));
    /// <inheritdoc />
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => new(_error.WriteAsync(value, cancellationToken));
    /// <inheritdoc />
    public async ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await Console.OpenStandardOutput().WriteAsync(value, cancellationToken).ConfigureAwait(false);
    }
}

// The dispatcher retains the original console. Handlers cannot prefix a JSON frame,
// even through the byte API, and cannot accidentally block a machine invocation.
internal sealed class MachineHandlerConsole(ICommandConsole inner) : ICommandConsole
{
    public bool IsInteractive => false;
    public bool IsInputRedirected => true;
    public bool IsOutputRedirected => true;
    public bool IsErrorRedirected => inner.IsErrorRedirected;
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(Encoding.UTF8.GetString(value.Span).AsMemory(), cancellationToken);
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => inner.WriteErrorAsync(value, cancellationToken);
}
