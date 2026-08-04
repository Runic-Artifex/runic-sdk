using System.Collections.Concurrent;
using System.Globalization;

namespace RunicCommandLine.Tests;

internal static class DispatcherTests
{
    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("dispatcher/typed-handler-receives-bound-options-and-context", TypedHandlerContext),
        new("dispatcher/success-disposes-async-scope-exactly-once", () => DisposalCase(Behavior.Success)),
        new("dispatcher/fault-disposes-async-scope-exactly-once", () => DisposalCase(Behavior.Fault)),
        new("dispatcher/exception-disposes-async-scope-exactly-once", () => DisposalCase(Behavior.Exception)),
        new("dispatcher/cancellation-disposes-async-scope-exactly-once", () => DisposalCase(Behavior.Cancellation)),
        new("dispatcher/binding-fault-skips-handler-and-disposes-scope", BindingFault),
        new("dispatcher/disposal-failure-takes-precedence", DisposalFailurePrecedence),
        new("dispatcher/concurrent-invocations-have-isolated-scopes", ConcurrentIsolation),
        new("dispatcher/observer-events-are-ordered-and-value-free", ObserverEvents),
        new("dispatcher/correlation-ids-match-wire-contract", CorrelationIdsMatchWireContract),
        new("dispatcher/fatal-handler-disposes-scope-and-propagates", FatalHandlerDisposesAndPropagates),
        new("dispatcher/fatal-observer-disposes-scope-and-propagates", FatalObserverDisposesAndPropagates),
        new("dispatcher/invalid-exit-policy-rejected-before-execution", InvalidExitPolicy),
    ];

    private static async ValueTask TypedHandlerContext()
    {
        CommandExecutionContext? seenContext = null;
        TestOptions? seenOptions = null;
        var expectedConsole = new MemoryCommandConsole();
        var binder = new TestBinder((invocation, _) =>
        {
            AssertEx.Equal("status", invocation.Path.ToString());
            return ValueTask.FromResult(CommandOutcome.Success(new TestOptions("typed")));
        });
        var factory = new TestHandlerFactory(_ => new TestHandler((options, context, _) =>
        {
            seenOptions = options;
            seenContext = context;
            return ValueTask.FromResult(CommandOutcome.Success(new TestResult(7, "typed")));
        }));
        var scopes = new TrackingScopeFactory();
        (CommandExecutor executor, CommandExecutionRequest request) = CreateExecution(
            binder,
            factory,
            scopes,
            expectedConsole,
            "correlation-typed");

        CommandExecutionResult result = await executor.ExecuteAsync(request, new CapturingSink());

        AssertEx.True(result.IsSuccess);
        AssertEx.Equal("typed", seenOptions!.Value);
        AssertEx.Equal("status", seenContext!.Path.ToString());
        AssertEx.Equal("correlation-typed", seenContext.CorrelationId);
        AssertEx.Equal(CultureInfo.InvariantCulture, seenContext.Culture);
        AssertEx.Equal(CommandOutputMode.Human, seenContext.OutputMode);
        AssertEx.True(ReferenceEquals(expectedConsole, seenContext.Console));
        AssertEx.True(ReferenceEquals(scopes.Scopes.Single().Services, seenContext.Services));
    }

    private static async ValueTask DisposalCase(Behavior behavior)
    {
        using var cancellation = new CancellationTokenSource();
        var scopes = new TrackingScopeFactory();
        var factory = new TestHandlerFactory(_ => new TestHandler((_, _, token) => behavior switch
        {
            Behavior.Success => ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok"))),
            Behavior.Fault => ValueTask.FromResult(CommandOutcome.Failure<TestResult>(
                CommandExitCategory.CommandFailure,
                new CommandFault("RCLI3000", "Expected failure."))),
            Behavior.Exception => ValueTask.FromException<CommandOutcome<TestResult>>(
                new InvalidOperationException("secret implementation failure")),
            Behavior.Cancellation => ValueTask.FromCanceled<CommandOutcome<TestResult>>(token),
            _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
        }));

        if (behavior == Behavior.Cancellation)
        {
            cancellation.Cancel();
        }

        (CommandExecutor executor, CommandExecutionRequest request) = CreateExecution(
            new TestBinder(), factory, scopes, new MemoryCommandConsole(), $"dispose-{behavior}");
        var sink = new CapturingSink();
        CommandExecutionResult result = await executor.ExecuteAsync(request, sink, cancellation.Token);

        CommandExitCategory expected = behavior switch
        {
            Behavior.Success => CommandExitCategory.Success,
            Behavior.Fault => CommandExitCategory.CommandFailure,
            Behavior.Exception => CommandExitCategory.HostFailure,
            Behavior.Cancellation => CommandExitCategory.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(behavior)),
        };
        AssertEx.Equal(expected, result.ExitCategory);
        AssertEx.Equal(1, scopes.Scopes.Single().DisposeCount);
        AssertEx.Equal(1, sink.WriteCount);
    }

    private static async ValueTask BindingFault()
    {
        int handlerCalls = 0;
        var binder = new TestBinder((_, _) => ValueTask.FromResult(CommandOutcome.Failure<TestOptions>(
            CommandExitCategory.Validation,
            new CommandFault("RCLI2001", "Options are invalid."))));
        var factory = new TestHandlerFactory(_ => new TestHandler((_, _, _) =>
        {
            Interlocked.Increment(ref handlerCalls);
            return ValueTask.FromResult(CommandOutcome.Success(new TestResult(0, "not-called")));
        }));
        var scopes = new TrackingScopeFactory();
        (CommandExecutor executor, CommandExecutionRequest request) = CreateExecution(
            binder, factory, scopes, new MemoryCommandConsole(), "binding-fault");

        CommandExecutionResult result = await executor.ExecuteAsync(request, new CapturingSink());

        AssertEx.Equal(CommandExitCategory.Validation, result.ExitCategory);
        AssertEx.Equal(0, handlerCalls);
        AssertEx.Equal(1, scopes.Scopes.Single().DisposeCount);
    }

    private static async ValueTask DisposalFailurePrecedence()
    {
        var scopes = new TrackingScopeFactory(_ => new TrackingScope(
            "failing-disposal",
            () => ValueTask.FromException(new InvalidOperationException("dispose failed"))));
        var factory = new TestHandlerFactory(_ => new TestHandler((_, _, _) =>
            ValueTask.FromResult(CommandOutcome.Failure<TestResult>(
                CommandExitCategory.CommandFailure,
                new CommandFault("RCLI3001", "Command failed.")))));
        (CommandExecutor executor, CommandExecutionRequest request) = CreateExecution(
            new TestBinder(), factory, scopes, new MemoryCommandConsole(), "dispose-precedence");
        var sink = new CapturingSink();

        CommandExecutionResult result = await executor.ExecuteAsync(request, sink);

        AssertEx.Equal(CommandExitCategory.HostFailure, result.ExitCategory);
        AssertEx.Equal("RCLI5000", result.Fault!.Code);
        AssertEx.Equal("RCLI5000", sink.FaultCode);
        AssertEx.Equal(1, scopes.Scopes.Single().DisposeCount);
    }

    private static async ValueTask ConcurrentIsolation()
    {
        const int Count = 64;
        var observations = new ConcurrentDictionary<string, (IServiceProvider Services, ICommandConsole Console)>();
        var scopes = new TrackingScopeFactory();
        var factory = new TestHandlerFactory(_ => new TestHandler((_, context, _) =>
        {
            AssertEx.True(observations.TryAdd(
                context.CorrelationId,
                (context.Services, context.Console)),
                "A correlation ID was reused.");
            return ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, context.CorrelationId)));
        }));
        CommandCatalog catalog = FixtureCatalog.Create(new TestBinder(), factory, new TestCodec());
        ParsedInvocation invocation = FixtureCatalog.ParseInvocation(catalog, "status");
        var executor = new CommandExecutor(scopes);

        Task[] executions = Enumerable.Range(0, Count).Select(async index =>
        {
            var request = new CommandExecutionRequest(
                invocation,
                new MemoryCommandConsole(),
                CultureInfo.InvariantCulture,
                $"concurrent-{index}");
            CommandExecutionResult result = await executor.ExecuteAsync(request, new CapturingSink());
            AssertEx.True(result.IsSuccess);
        }).ToArray();
        await Task.WhenAll(executions);

        AssertEx.Equal(Count, observations.Count);
        AssertEx.Equal(Count, observations.Values.Select(static item => item.Services).Distinct().Count());
        AssertEx.Equal(Count, observations.Values.Select(static item => item.Console).Distinct().Count());
        AssertEx.Equal(Count, scopes.Scopes.Count);
        AssertEx.True(scopes.Scopes.All(static scope => scope.DisposeCount == 1));
    }

    private static async ValueTask ObserverEvents()
    {
        var observer = new RecordingObserver();
        var scopes = new TrackingScopeFactory();
        (CommandExecutor _, CommandExecutionRequest request) = CreateExecution(
            new TestBinder(), new TestHandlerFactory(), scopes, new MemoryCommandConsole(), "safe-id");
        var executor = new CommandExecutor(scopes, observer: observer);

        await executor.ExecuteAsync(request, new CapturingSink());

        AssertEx.SequenceEqual(
            new[]
            {
                CommandExecutionEventKind.Started,
                CommandExecutionEventKind.Bound,
                CommandExecutionEventKind.HandlerStarted,
                CommandExecutionEventKind.Completed,
            },
            observer.Events.Select(static item => item.Kind));
        AssertEx.True(observer.Events.All(static item => item.CorrelationId == "safe-id"));
        AssertEx.True(observer.Events.All(static item => item.Path.ToString() == "status"));
    }

    private static ValueTask InvalidExitPolicy()
    {
        AssertEx.Throws<ArgumentException>(() =>
            _ = new CommandExecutor(new TrackingScopeFactory(), new ZeroExitPolicy()));
        return ValueTask.CompletedTask;
    }

    private static ValueTask CorrelationIdsMatchWireContract()
    {
        CommandCatalog catalog = FixtureCatalog.Create(new TestBinder(), new TestHandlerFactory(), new TestCodec());
        ParsedInvocation invocation = FixtureCatalog.ParseInvocation(catalog, "status");
        var console = new MemoryCommandConsole();

        _ = new CommandExecutionRequest(invocation, console, CultureInfo.InvariantCulture, new string('a', 128));
        AssertEx.Throws<ArgumentException>(() =>
            _ = new CommandExecutionRequest(invocation, console, CultureInfo.InvariantCulture, new string('\u00e9', 65)));
        AssertEx.Throws<ArgumentException>(() =>
            _ = new CommandExecutionRequest(invocation, console, CultureInfo.InvariantCulture, "bad\ud800"));
        AssertEx.Throws<ArgumentException>(() =>
            _ = new CommandExecutionRequest(invocation, console, CultureInfo.InvariantCulture, "bad\ufdd0"));

        return ValueTask.CompletedTask;
    }

    private static async ValueTask FatalHandlerDisposesAndPropagates()
    {
        var scopes = new TrackingScopeFactory();
        var factory = new TestHandlerFactory(_ => new TestHandler((_, _, _) =>
            ValueTask.FromException<CommandOutcome<TestResult>>(new FatalTestException("fatal"))));
        (CommandExecutor executor, CommandExecutionRequest request) = CreateExecution(
            new TestBinder(), factory, scopes, new MemoryCommandConsole(), "fatal-handler");

        await AssertEx.ThrowsAsync<FatalTestException>(async () =>
            _ = await executor.ExecuteAsync(request, new CapturingSink()));
        AssertEx.Equal(1, scopes.Scopes.Single().DisposeCount);
    }

    private static async ValueTask FatalObserverDisposesAndPropagates()
    {
        var scopes = new TrackingScopeFactory();
        (CommandExecutor _, CommandExecutionRequest request) = CreateExecution(
            new TestBinder(), new TestHandlerFactory(), scopes, new MemoryCommandConsole(), "fatal-observer");
        var executor = new CommandExecutor(scopes, observer: new FatalObserver());

        await AssertEx.ThrowsAsync<FatalTestException>(async () =>
            _ = await executor.ExecuteAsync(request, new CapturingSink()));
        AssertEx.Equal(1, scopes.Scopes.Single().DisposeCount);
    }

    private static (CommandExecutor Executor, CommandExecutionRequest Request) CreateExecution(
        TestBinder binder,
        TestHandlerFactory handlerFactory,
        TrackingScopeFactory scopeFactory,
        MemoryCommandConsole console,
        string correlationId)
    {
        CommandCatalog catalog = FixtureCatalog.Create(binder, handlerFactory, new TestCodec());
        ParsedInvocation invocation = FixtureCatalog.ParseInvocation(catalog, "status");
        return (
            new CommandExecutor(scopeFactory),
            new CommandExecutionRequest(
                invocation,
                console,
                CultureInfo.InvariantCulture,
                correlationId));
    }

    private enum Behavior
    {
        Success,
        Fault,
        Exception,
        Cancellation,
    }

    private sealed class RecordingObserver : ICommandExecutionObserver
    {
        public List<CommandExecutionEvent> Events { get; } = [];

        public void Observe(CommandExecutionEvent executionEvent) => Events.Add(executionEvent);
    }

    private sealed class FatalObserver : ICommandExecutionObserver
    {
        public void Observe(CommandExecutionEvent executionEvent) =>
            throw new FatalTestException("fatal observer");
    }

    private sealed class FatalTestException : OutOfMemoryException
    {
        public FatalTestException(string message)
            : base(message)
        {
        }
    }

    private sealed class ZeroExitPolicy : IExitCodePolicy
    {
        public int GetExitCode(CommandExitCategory category) => 0;
    }
}
