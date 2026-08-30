using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Hosting;

return await HostingAdapterTests.RunAsync();

internal static class HostingAdapterTests
{
    public static async Task<int> RunAsync()
    {
        (string Name, Func<ValueTask> Body)[] tests =
        [
            ("classification/is-pure-and-maps-parser-outcomes", ClassificationIsPure),
            ("classification/replays-captured-output-inputs", CapturedOutputPrecedence),
            ("classification/error-preserves-transport-output", ErrorPreservesTransportOutput),
            ("classification/uses-configured-transport-output-option", ConfiguredTransportOutput),
            ("classification/rejects-built-in-transport-output-option", BuiltInTransportOutputIsRejected),
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

    private static ValueTask ErrorPreservesTransportOutput()
    {
        CommandCatalog catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        var bridge = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));

        HostedCommandLineDecision decision = bridge.Classify(new(
            ["run", "--runic-output=JSON", "--unknown=secret"],
            transportOutputOptionName: "--runic-output"));

        AssertEqual(HostedCommandLineDecisionKind.Invalid, decision.Kind);
        AssertEqual(CommandOutputMode.Json, decision.OutputClassification!.Value.Mode);
        AssertEqual(CommandOutputModeSource.ExplicitArgument, decision.OutputClassification.Value.Source);
        AssertEqual("--unknown", decision.Diagnostics[0].Arguments[0]);

        HostedCommandLineDecision knownErrorBeforeTransport = bridge.Classify(new(
            ["run", "--unknown=secret", "--runic-output=JSON"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, knownErrorBeforeTransport.Kind);
        AssertEqual(CommandOutputMode.Json, knownErrorBeforeTransport.OutputClassification!.Value.Mode);
        AssertEqual("--unknown", knownErrorBeforeTransport.Diagnostics[0].Arguments[0]);

        HostedCommandLineDecision hostileRoot = bridge.Classify(new(
            ["--runic-output=TOPSECRET"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, hostileRoot.Kind);
        AssertEqual(CommandOutputMode.Human, hostileRoot.OutputClassification!.Value.Mode);
        AssertEqual("RCLI1010", hostileRoot.Diagnostics[0].Code);
        AssertEqual(0, hostileRoot.Diagnostics[0].Arguments.Count);
        AssertTrue(!hostileRoot.Diagnostics[0].Message.Contains("TOPSECRET", StringComparison.Ordinal));

        foreach (string[] args in new[]
        {
            new[] { "--help", "--runic-output=JSON" },
            new[] { "--runic-output=JSON", "--help" },
            new[] { "help", "--runic-output=JSON" },
            new[] { "--runic-output=JSON", "help" },
        })
        {
            HostedCommandLineDecision help = bridge.Classify(new(args, transportOutputOptionName: "--runic-output"));
            AssertEqual(HostedCommandLineDecisionKind.Help, help.Kind);
            AssertEqual(CommandOutputMode.Json, help.OutputClassification!.Value.Mode);
        }

        HostedCommandLineDecision rootHelpExtra = bridge.Classify(new(
            ["help", "--unknown=secret", "--runic-output=JSON"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, rootHelpExtra.Kind);
        AssertEqual(CommandOutputMode.Json, rootHelpExtra.OutputClassification!.Value.Mode);
        AssertEqual("RCLI1013", rootHelpExtra.Diagnostics[0].Code);
        AssertEqual(0, rootHelpExtra.Diagnostics[0].Arguments.Count);
        AssertTrue(!rootHelpExtra.Diagnostics[0].Message.Contains("secret", StringComparison.Ordinal));

        foreach (string[] args in new[]
        {
            new[] { "--version", "--runic-output=JSON" },
            new[] { "--runic-output=JSON", "--version" },
        })
        {
            HostedCommandLineDecision version = bridge.Classify(new(args, transportOutputOptionName: "--runic-output"));
            AssertEqual(HostedCommandLineDecisionKind.Version, version.Kind);
            AssertEqual(CommandOutputMode.Json, version.OutputClassification!.Value.Mode);
        }

        foreach (string[] args in new[]
        {
            new[] { "run", "--help", "--runic-output=JSON" },
            new[] { "run", "--runic-output=JSON", "--help" },
        })
        {
            HostedCommandLineDecision help = bridge.Classify(new(args, transportOutputOptionName: "--runic-output"));
            AssertEqual(HostedCommandLineDecisionKind.Help, help.Kind);
            AssertEqual(CommandOutputMode.Json, help.OutputClassification!.Value.Mode);
        }

        HostedCommandLineDecision slashAliasBoundary = bridge.Classify(new(
            ["run", "--runic-output", "/x"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, slashAliasBoundary.Kind);
        AssertEqual("RCLI1003", slashAliasBoundary.Diagnostics[0].Code);
        AssertEqual("--runic-output", slashAliasBoundary.Diagnostics[0].Arguments[0]);

        HostedCommandLineDecision variadicSingleOccurrence = bridge.Classify(new(
            ["run", "--documents", "a", "b"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invocation, variadicSingleOccurrence.Kind);

        HostedCommandLineDecision variadicRepeated = bridge.Classify(new(
            ["run", "--documents", "a", "--documents", "b"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invalid, variadicRepeated.Kind);
        AssertEqual("RCLI1007", variadicRepeated.Diagnostics[0].Code);
        AssertEqual("--documents", variadicRepeated.Diagnostics[0].Arguments[0]);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ConfiguredTransportOutput()
    {
        var binder = new DelegateBinder();
        var handlers = new TrackingHandlerFactory();
        CommandCatalog catalog = new CommandCatalogBuilder()
            .Command<Options, Handler, Result>("run", command => command
                .Option("output", "--output", CommandArity.ExactlyOne)
                .BindWith(binder)
                .CreateHandlerWith(handlers)
                .Produces(ResultCodec.Instance))
            .Build();
        var bridge = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));

        HostedCommandLineDecision decision = bridge.Classify(new(
            ["run", "--output", "application", "--runic-output", "json"],
            transportOutputOptionName: "--runic-output"));
        AssertEqual(HostedCommandLineDecisionKind.Invocation, decision.Kind);
        AssertEqual(CommandOutputMode.Json, decision.OutputClassification!.Value.Mode);
        return ValueTask.CompletedTask;
    }

    private static ValueTask BuiltInTransportOutputIsRejected()
    {
        foreach (string spelling in new[] { "--help", "--version" })
        {
            AssertThrows<ArgumentException>(() =>
            {
                _ = new HostedCommandLineLaunchInput(
                    ["run"],
                    transportOutputOptionName: spelling);
            });
        }

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
                .Option("value", "--value", CommandArity.ExactlyOne, aliases: ["/x"])
                .Option("documents", "--documents", CommandArity.OneOrMore, CommandOptionRepeatPolicy.Error)
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

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
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
