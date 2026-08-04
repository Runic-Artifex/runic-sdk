using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using RunicCommandLine;
using RunicCommandLine.Hosting;

return await HostingAdapterTests.RunAsync();

internal static class HostingAdapterTests
{
    public static async Task<int> RunAsync()
    {
        (string Name, Func<ValueTask> Body)[] tests =
        [
            ("classification/is-pure-and-maps-parser-outcomes", ClassificationIsPure),
            ("classification/replays-captured-output-inputs", CapturedOutputPrecedence),
            ("classification/empty-ui-fallback-is-explicit", EmptyInputFallbackIsExplicit),
            ("execution/delegates-once-and-preserves-result", ExecutionDelegatesOnce),
            ("execution/rejects-a-decision-created-by-another-adapter", ExecutionRejectsMismatchedAdapter),
            ("execution/preserves-executor-fault-and-cancellation-precedence", ExecutionPreservesPrecedence),
        ];
        int failures = 0;
        foreach ((string name, Func<ValueTask> body) in tests)
        {
            try
            {
                await body().ConfigureAwait(false);
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                failures++;
            }
        }

        Console.WriteLine($"SUMMARY passed={tests.Length - failures} failed={failures} total={tests.Length}");
        return failures == 0 ? 0 : 1;
    }

    private static ValueTask ClassificationIsPure()
    {
        var handlerFactory = new TrackingHandlerFactory();
        var scopes = new TrackingScopeFactory();
        CommandCatalog catalog = CreateCatalog(new DelegateBinder(), handlerFactory);
        var syntax = new CountingSyntaxAdapter();
        var bridge = new CommandLineHostingAdapter(catalog, new CommandExecutor(scopes), syntax);

        AssertEqual(HostedCommandLineDecisionKind.Invocation, bridge.Classify(new(["run"])).Kind);
        AssertEqual(HostedCommandLineDecisionKind.Help, bridge.Classify(new(["--help"])).Kind);
        AssertEqual(HostedCommandLineDecisionKind.Version, bridge.Classify(new(["--version"])).Kind);
        HostedCommandLineDecision invalid = bridge.Classify(new(["unknown"]));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, invalid.Kind);
        AssertEqual("RCLI1002", invalid.Diagnostics[0].Code);
        AssertEqual(4, syntax.ParseCalls);
        AssertEqual(0, scopes.Created);
        AssertEqual(0, handlerFactory.Created);
        return ValueTask.CompletedTask;
    }

    private static ValueTask CapturedOutputPrecedence()
    {
        CommandCatalog catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        var bridge = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));

        HostedCommandLineDecision explicitOutput = bridge.Classify(
            new(["run", "--output", "human"], outputEnvironmentValue: "json"));
        AssertEqual(CommandOutputMode.Human, explicitOutput.OutputClassification!.Value.Mode);
        AssertEqual(CommandOutputModeSource.ExplicitArgument, explicitOutput.OutputClassification.Value.Source);

        HostedCommandLineDecision capturedEnvironment = bridge.Classify(
            new(["run"], outputEnvironmentValue: "json"));
        AssertEqual(CommandOutputMode.Json, capturedEnvironment.OutputClassification!.Value.Mode);
        AssertEqual(CommandOutputModeSource.Environment, capturedEnvironment.OutputClassification.Value.Source);

        HostedCommandLineDecision defaultOutput = bridge.Classify(new(["run"]));
        AssertEqual(CommandOutputMode.Human, defaultOutput.OutputClassification!.Value.Mode);
        AssertEqual(CommandOutputModeSource.Default, defaultOutput.OutputClassification.Value.Source);

        return ValueTask.CompletedTask;
    }

    private static ValueTask EmptyInputFallbackIsExplicit()
    {
        CommandCatalog catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        var bridge = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));

        HostedCommandLineDecision invalid = bridge.Classify(new(Array.Empty<string>()));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, invalid.Kind);
        AssertEqual("RCLI1002", invalid.Diagnostics[0].Code);

        HostedCommandLineDecision userInterface = bridge.Classify(
            new(Array.Empty<string>(), emptyInputFallback: EmptyInputFallback.UserInterface));
        AssertEqual(HostedCommandLineDecisionKind.UserInterface, userInterface.Kind);
        AssertEqual(0, userInterface.Diagnostics.Count);

        HostedCommandLineDecision unknown = bridge.Classify(
            new(["not-a-command"], emptyInputFallback: EmptyInputFallback.UserInterface));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, unknown.Kind);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask ExecutionDelegatesOnce()
    {
        var handlerFactory = new TrackingHandlerFactory();
        var scopes = new TrackingScopeFactory();
        var bridge = new CommandLineHostingAdapter(
            CreateCatalog(new DelegateBinder(), handlerFactory),
            new CommandExecutor(scopes));
        HostedCommandLineDecision decision = bridge.Classify(new(["run"]));
        var sink = new CapturingSink();

        HostedCommandLineExecutionResult result = await bridge.ExecuteAsync(
            new(decision, new SilentConsole(), CultureInfo.InvariantCulture, "bridge-success", sink));

        AssertTrue(result.IsSuccess);
        AssertEqual(CommandExitCodes.Success, result.ExitCode);
        AssertEqual(CommandExitCategory.Success, result.ExitCategory);
        AssertEqual(1, scopes.Created);
        AssertEqual(1, scopes.Scope!.Disposals);
        AssertEqual(1, handlerFactory.Created);
        AssertEqual(1, sink.Writes);
    }

    private static async ValueTask ExecutionRejectsMismatchedAdapter()
    {
        CommandCatalog catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        var first = new CommandLineHostingAdapter(
            catalog,
            new CommandExecutor(new TrackingScopeFactory()));
        var secondScopes = new TrackingScopeFactory();
        var second = new CommandLineHostingAdapter(
            catalog,
            new CommandExecutor(secondScopes));
        HostedCommandLineDecision decision = first.Classify(new(["run"]));

        try
        {
            await second.ExecuteAsync(
                new(decision, new SilentConsole(), CultureInfo.InvariantCulture, "bridge-mismatch", new CapturingSink()));
            throw new InvalidOperationException("A mismatched adapter unexpectedly executed the decision.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("adapter that created it", StringComparison.Ordinal))
        {
            AssertEqual(0, secondScopes.Created);
        }
    }

    private static async ValueTask ExecutionPreservesPrecedence()
    {
        var binderFailureScopes = new TrackingScopeFactory();
        var binderFailureHandler = new TrackingHandlerFactory();
        var binderFailureBridge = new CommandLineHostingAdapter(
            CreateCatalog(new DelegateBinder((_, _) => ValueTask.FromResult(CommandOutcome.Failure<Options>(
                CommandExitCategory.Validation,
                new CommandFault("RCLI2001", "Options are invalid.")))), binderFailureHandler),
            new CommandExecutor(binderFailureScopes));
        HostedCommandLineExecutionResult binding = await ExecuteAsync(binderFailureBridge);
        AssertEqual(CommandExitCategory.Validation, binding.ExitCategory);
        AssertEqual(CommandExitCodes.Validation, binding.ExitCode);
        AssertEqual(0, binderFailureHandler.Created);
        AssertEqual(1, binderFailureScopes.Scope!.Disposals);

        var handlerFailureScopes = new TrackingScopeFactory();
        var handlerFailureBridge = new CommandLineHostingAdapter(
            CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory((_, _, _) => ValueTask.FromResult(
                CommandOutcome.Failure<Result>(
                    CommandExitCategory.CommandFailure,
                    new CommandFault("RCLI3000", "The command failed."))))),
            new CommandExecutor(handlerFailureScopes));
        HostedCommandLineExecutionResult handler = await ExecuteAsync(handlerFailureBridge);
        AssertEqual(CommandExitCategory.CommandFailure, handler.ExitCategory);
        AssertEqual(CommandExitCodes.CommandFailure, handler.ExitCode);
        AssertEqual("RCLI3000", handler.Fault!.Code);

        var outputFailureScopes = new TrackingScopeFactory();
        var outputFailureBridge = new CommandLineHostingAdapter(
            CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory()),
            new CommandExecutor(outputFailureScopes));
        HostedCommandLineDecision outputDecision = outputFailureBridge.Classify(new(["run"]));
        HostedCommandLineExecutionResult output = await outputFailureBridge.ExecuteAsync(
            new(outputDecision, new SilentConsole(), CultureInfo.InvariantCulture, "bridge-output", new ThrowingSink()));
        AssertEqual(CommandExitCategory.HostFailure, output.ExitCategory);
        AssertEqual(CommandExitCodes.HostFailure, output.ExitCode);

        var cancellationScopes = new TrackingScopeFactory();
        var cancellationBridge = new CommandLineHostingAdapter(
            CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory()),
            new CommandExecutor(cancellationScopes));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        HostedCommandLineExecutionResult cancelled = await ExecuteAsync(cancellationBridge, cancellation.Token);
        AssertEqual(CommandExitCategory.Cancelled, cancelled.ExitCategory);
        AssertEqual(CommandExitCodes.Cancelled, cancelled.ExitCode);
        AssertEqual(1, cancellationScopes.Scope!.Disposals);
    }

    private static async ValueTask<HostedCommandLineExecutionResult> ExecuteAsync(
        CommandLineHostingAdapter bridge,
        CancellationToken cancellationToken = default)
    {
        HostedCommandLineDecision decision = bridge.Classify(new(["run"]));
        return await bridge.ExecuteAsync(
            new(decision, new SilentConsole(), CultureInfo.InvariantCulture, "bridge-failure", new CapturingSink()),
            cancellationToken);
    }

    private static CommandCatalog CreateCatalog(
        DelegateBinder binder,
        TrackingHandlerFactory handlerFactory) => new CommandCatalogBuilder()
            .Command<Options, Handler, Result>("run", command => command
                .BindWith(binder)
                .CreateHandlerWith(handlerFactory)
                .Produces(ResultCodec.Instance))
            .Build();

    private static void AssertEqual<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected <{expected}> but found <{actual}>.");
        }
    }

    private static void AssertTrue(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected condition to be true.");
        }
    }
}

internal sealed class Options;
internal sealed record Result(int Value);

internal sealed class DelegateBinder : ICommandOptionsBinder<Options>
{
    private readonly Func<ParsedInvocation, CancellationToken, ValueTask<CommandOutcome<Options>>> _bind;

    public DelegateBinder(Func<ParsedInvocation, CancellationToken, ValueTask<CommandOutcome<Options>>>? bind = null) =>
        _bind = bind ?? ((_, _) => ValueTask.FromResult(CommandOutcome.Success(new Options())));

    public ValueTask<CommandOutcome<Options>> BindAsync(ParsedInvocation invocation, CancellationToken cancellationToken) =>
        _bind(invocation, cancellationToken);
}

internal sealed class Handler : ICommandHandler<Options, Result>
{
    private readonly Func<Options, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<Result>>> _execute;

    public Handler(Func<Options, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<Result>>> execute) =>
        _execute = execute;

    public ValueTask<CommandOutcome<Result>> ExecuteAsync(
        Options options,
        CommandExecutionContext context,
        CancellationToken cancellationToken) => _execute(options, context, cancellationToken);
}

internal sealed class TrackingHandlerFactory : ICommandHandlerFactory<Handler>
{
    private readonly Func<Options, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<Result>>> _execute;

    public TrackingHandlerFactory(
        Func<Options, CommandExecutionContext, CancellationToken, ValueTask<CommandOutcome<Result>>>? execute = null) =>
        _execute = execute ?? ((_, _, _) => ValueTask.FromResult(CommandOutcome.Success(new Result(42))));

    public int Created { get; private set; }

    public Handler Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Created++;
        return new Handler(_execute);
    }
}

internal sealed class TrackingScopeFactory : ICommandExecutionScopeFactory
{
    public int Created { get; private set; }
    public TrackingScope? Scope { get; private set; }

    public ICommandExecutionScope CreateScope()
    {
        Created++;
        Scope = new TrackingScope();
        return Scope;
    }
}

internal sealed class TrackingScope : ICommandExecutionScope, IServiceProvider
{
    public int Disposals { get; private set; }
    public IServiceProvider Services => this;
    public object? GetService(Type serviceType) => null;
    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class CountingSyntaxAdapter : ICommandSyntaxAdapter
{
    public int ParseCalls { get; private set; }

    public ParseOutcome Parse(CommandCatalog catalog, ReadOnlySpan<string> args, ParseSettings settings)
    {
        ParseCalls++;
        return PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, settings);
    }
}

internal sealed class CapturingSink : ICommandOutcomeSink
{
    public int Writes { get; private set; }

    public ValueTask WriteAsync<T>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<T> outcome,
        ICommandResultCodec<T> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        Writes++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class ThrowingSink : ICommandOutcomeSink
{
    public ValueTask WriteAsync<T>(
        CommandDescriptor command,
        CommandExecutionContext context,
        CommandOutcome<T> outcome,
        ICommandResultCodec<T> codec,
        int exitCode,
        IReadOnlyList<CommandDiagnostic> diagnostics,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException("output failed"));
}

internal sealed class SilentConsole : ICommandConsole
{
    public bool IsInteractive => false;
    public bool IsInputRedirected => true;
    public bool IsOutputRedirected => true;
    public bool IsErrorRedirected => true;
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class ResultCodec : ICommandResultCodec<Result>
{
    public static ResultCodec Instance { get; } = new();
    public string PayloadType => "tests.hosting/1";
    public JsonTypeInfo<Result> TypeInfo => TestJsonContext.Default.Result;
    public ValueTask WriteHumanAsync(Result value, ICommandConsole console, CultureInfo culture, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

[JsonSerializable(typeof(Result))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
