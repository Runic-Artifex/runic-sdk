using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine.Testing;

/// <summary>Provides a deterministic in-memory console for command tests.</summary>
public sealed class TestCommandConsole : ICommandConsole
{
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();

    /// <summary>Gets captured standard output.</summary>
    public string StandardOutput => _standardOutput.ToString();

    /// <summary>Gets captured standard error.</summary>
    public string StandardError => _standardError.ToString();

    /// <inheritdoc />
    public bool IsInteractive => false;
    /// <inheritdoc />
    public bool IsInputRedirected => true;
    /// <inheritdoc />
    public bool IsOutputRedirected => true;
    /// <inheritdoc />
    public bool IsErrorRedirected => true;

    /// <inheritdoc />
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _standardOutput.Append(value.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _standardOutput.Append(Encoding.UTF8.GetString(value.Span));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _standardError.Append(value.Span);
        return ValueTask.CompletedTask;
    }
}
