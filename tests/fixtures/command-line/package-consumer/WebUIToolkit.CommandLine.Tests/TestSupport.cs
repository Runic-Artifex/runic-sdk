using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WebUIToolkit.CommandLine.Tests;

internal sealed record TestOptions(string Value = "bound");

internal sealed record TestResult(int Value, string Text);

internal sealed class TestBinder : ICommandOptionsBinder<TestOptions>
{
    private readonly Func<ParsedInvocation, CancellationToken, ValueTask<CommandOutcome<TestOptions>>> _bind;

    public TestBinder(Func<ParsedInvocation, CancellationToken, ValueTask<CommandOutcome<TestOptions>>>? bind = null)
    {
        _bind = bind ?? ((_, _) => ValueTask.FromResult(CommandOutcome.Success(new TestOptions())));
    }

    public ValueTask<CommandOutcome<TestOptions>> BindAsync(
        ParsedInvocation invocation,
        CancellationToken cancellationToken) => _bind(invocation, cancellationToken);
}

internal sealed class TestHandler : ICommandHandler<TestOptions, TestResult>
{
    private readonly Func<TestOptions, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<TestResult>>> _execute;

    public TestHandler(
        Func<TestOptions, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<TestResult>>>? execute = null)
    {
        _execute = execute ?? ((_, _, _) => ValueTask.FromResult(
            CommandOutcome.Success(new TestResult(42, "ok"))));
    }

    public ValueTask<CommandOutcome<TestResult>> ExecuteAsync(
        TestOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken) => _execute(options, context, cancellationToken);
}

internal sealed class TestHandlerFactory : ICommandHandlerFactory<TestHandler>
{
    private readonly Func<IServiceProvider, TestHandler> _create;

    public TestHandlerFactory(Func<IServiceProvider, TestHandler>? create = null)
    {
        _create = create ?? (_ => new TestHandler());
    }

    public TestHandler Create(IServiceProvider services) => _create(services);
}

internal sealed class TestCodec : ICommandResultCodec<TestResult>
{
    public const string Identity = "tests.result/1";

    public string PayloadType => Identity;

    public JsonTypeInfo<TestResult> TypeInfo => TestJsonContext.Default.TestResult;

    public ValueTask WriteHumanAsync(
        TestResult value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken) =>
        console.WriteOutAsync($"{value.Value.ToString(culture)}:{value.Text}\n".AsMemory(), cancellationToken);
}

internal sealed class MemoryCommandConsole : ICommandConsole
{
    private readonly object _gate = new();
    private readonly StringBuilder _out = new();
    private readonly StringBuilder _error = new();

    public bool IsInteractive => false;

    public bool IsInputRedirected => true;

    public bool IsOutputRedirected => true;

    public bool IsErrorRedirected => true;

    public string StandardOutput
    {
        get { lock (_gate) { return _out.ToString(); } }
    }

    public string StandardError
    {
        get { lock (_gate) { return _error.ToString(); } }
    }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _out.Append(value.Span); }
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _out.Append(Encoding.UTF8.GetString(value.Span)); }
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _error.Append(value.Span); }
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

internal sealed class TrackingScope : ICommandExecutionScope
{
    private readonly Func<ValueTask>? _dispose;
    private int _disposeCount;

    public TrackingScope(string identity, Func<ValueTask>? dispose = null)
    {
        Identity = identity;
        _dispose = dispose;
    }

    public string Identity { get; }

    public IServiceProvider Services { get; } = new TestServiceProvider();

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        if (_dispose is not null)
        {
            await _dispose();
        }
    }
}

internal sealed class TrackingScopeFactory : ICommandExecutionScopeFactory
{
    private readonly Func<int, TrackingScope> _create;
    private int _created;

    public TrackingScopeFactory(Func<int, TrackingScope>? create = null)
    {
        _create = create ?? (index => new TrackingScope($"scope-{index}"));
    }

    public ConcurrentQueue<TrackingScope> Scopes { get; } = new();

    public ICommandExecutionScope CreateScope()
    {
        TrackingScope scope = _create(Interlocked.Increment(ref _created));
        Scopes.Enqueue(scope);
        return scope;
    }
}

internal sealed class CapturingSink : ICommandOutcomeSink
{
    private int _writes;

    public int WriteCount => Volatile.Read(ref _writes);

    public CommandExitCategory? Category { get; private set; }

    public int? ExitCode { get; private set; }

    public string? FaultCode { get; private set; }

    public string? PayloadType { get; private set; }

    public object? TypeInfo { get; private set; }

    public ValueTask WriteAsync<TResult>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<TResult> outcome,
        ICommandResultCodec<TResult> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _writes);
        Category = outcome.ExitCategory;
        ExitCode = exitCode;
        FaultCode = outcome.Fault?.Code;
        PayloadType = codec.PayloadType;
        TypeInfo = codec.TypeInfo;
        return ValueTask.CompletedTask;
    }
}

[JsonSerializable(typeof(TestResult))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
