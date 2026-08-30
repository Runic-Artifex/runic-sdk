using System;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Examples.GreetingCommandLine;

internal sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
{
    internal static EmptyScopeFactory Instance { get; } = new();

    private EmptyScopeFactory()
    {
    }

    public ICommandExecutionScope CreateScope() => new EmptyScope();

    private sealed class EmptyScope : ICommandExecutionScope, IServiceProvider
    {
        public IServiceProvider Services => this;

        public object? GetService(Type serviceType) => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class SystemCommandConsole : ICommandConsole
{
    public bool IsInteractive =>
        !Console.IsInputRedirected && !Console.IsOutputRedirected && !Console.IsErrorRedirected;

    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public bool IsErrorRedirected => Console.IsErrorRedirected;

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        Console.In.ReadLineAsync(cancellationToken);

    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        new(Console.Out.WriteAsync(value, cancellationToken));

    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) =>
        Console.OpenStandardOutput().WriteAsync(value, cancellationToken);

    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) =>
        new(Console.Error.WriteAsync(value, cancellationToken));
}
