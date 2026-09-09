using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Runs a catalog without requiring a dependency-injection container or Generic Host.</summary>
public sealed class CommandApp
{
    private readonly CommandCatalog _catalog;
    /// <summary>Initializes an application from an immutable catalog.</summary>
    public CommandApp(CommandCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    /// <summary>Gets or sets the executable name shown in help.</summary>
    public string Name { get; init; } = "app";
    /// <summary>Gets or sets the executable name registered by completion scripts, when different from Name.</summary>
    public string? CompletionExecutableName { get; init; }
    /// <summary>Gets or sets the application version.</summary>
    public string Version { get; init; } = "0.0.0";
    /// <summary>Gets or sets invocation-local console IO.</summary>
    public ICommandConsole Console { get; init; } = new SystemCommandConsole();
    /// <summary>Gets or sets an optional scope factory.</summary>
    public ICommandExecutionScopeFactory? ScopeFactory { get; init; }
    /// <summary>Gets or sets an invocation-dependent scope factory for applications with launch-specific services.</summary>
    public Func<ParsedInvocation, ICommandExecutionScopeFactory>? CreateScopeFactory { get; init; }
    /// <summary>Gets or sets a presenter for framework requests when preserving an existing application wire contract.</summary>
    public Func<ParseOutcome, ICommandConsole, CancellationToken, ValueTask<int>>? PresentFrameworkRequest { get; init; }
    /// <summary>Gets or sets the exit policy, including parser errors.</summary>
    public IExitCodePolicy ExitCodePolicy { get; init; } = DefaultExitCodePolicy.Instance;
    /// <summary>Gets or sets the outcome presenter.</summary>
    public ICommandOutcomeSink OutcomeSink { get; init; } = new CommandOutputDispatcher();
    /// <summary>Gets or sets explicit parse settings. Otherwise output configuration is read at invocation time.</summary>
    public ParseSettings? ParseSettings { get; init; }
    /// <summary>Gets or sets an observer for internal exceptions; these are never displayed to users.</summary>
    public Action<Exception>? ExceptionObserver { get; init; }
    /// <summary>Gets or sets an execution lifecycle observer.</summary>
    public ICommandExecutionObserver? Observer { get; init; }
    /// <summary>Gets or sets whether process Ctrl+C cancels the invocation.</summary>
    public bool HandleCancelKeyPress { get; init; } = true;
    /// <summary>Gets or sets a renderer for human catalog help.</summary>
    public ICommandHelpPresenter? HelpPresenter { get; init; }
    /// <summary>Gets or sets an optional application-specific help formatter.</summary>
    public Func<CommandPath, string>? FormatHelp { get; init; }

    /// <summary>Parses, presents framework requests, or executes one invocation, returning its process exit code.</summary>
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        if (HandleCancelKeyPress) System.Console.CancelKeyPress += handler;
        try
        {
            var presentation = new CommandPresentation
            {
                Name = Name, Version = Version, CompletionExecutableName = CompletionExecutableName,
                HelpPresenter = HelpPresenter, FormatHelp = FormatHelp, ExitCodePolicy = ExitCodePolicy, ExceptionObserver = ExceptionObserver,
            };
            ParseSettings settings = ParseSettings ?? new ParseSettings(Environment.GetEnvironmentVariable(CommandOutputClassifier.EnvironmentVariableName)) { GetEnvironmentVariable = Environment.GetEnvironmentVariable };
            if (args.Length == 2 && args[0] == "completion" && !_catalog.TryGetCommand("completion", out _))
            {
                return await presentation.GuardAsync(() => presentation.WriteCompletionAsync(_catalog, args[1], settings.TransportOutputOptionName, Console, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            }
            ParseOutcome parsed = PortableCommandSyntaxAdapter.Instance.Parse(_catalog, args.Length == 0 && _catalog.DefaultCommand is null ? ["--help"] : args, settings);
            string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            if (parsed.Kind == ParseOutcomeKind.Invocation)
            {
                ICommandExecutionScopeFactory scopes = CreateScopeFactory is { } createScopes
                    ? new InvocationScopeFactory(createScopes, parsed.Invocation!)
                    : ScopeFactory ?? EmptyScopeFactory.Instance;
                var executor = new CommandExecutor(scopes, ExitCodePolicy, Observer);
                CommandExecutionResult result = await executor.ExecuteAsync(
                    new CommandExecutionRequest(parsed.Invocation!, Console, CultureInfo.CurrentCulture, requestId) { ExceptionObserver = ExceptionObserver },
                    OutcomeSink, cancellation.Token).ConfigureAwait(false);
                return result.ExitCode;
            }

            if (PresentFrameworkRequest is not null)
                return await presentation.GuardAsync(() => PresentFrameworkRequest(parsed, Console, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            return await presentation.GuardAsync(() => presentation.WriteAsync(_catalog, parsed, settings.TransportOutputOptionName,
                Console, CultureInfo.CurrentCulture, requestId, cancellation.Token), cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            if (HandleCancelKeyPress) System.Console.CancelKeyPress -= handler;
        }
    }

    private sealed class InvocationScopeFactory(Func<ParsedInvocation, ICommandExecutionScopeFactory> create, ParsedInvocation invocation) : ICommandExecutionScopeFactory
    {
        public ICommandExecutionScope CreateScope() => create(invocation).CreateScope();
    }

    private sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
    {
        internal static EmptyScopeFactory Instance { get; } = new();
        public ICommandExecutionScope CreateScope() => new EmptyScope();
    }
    private sealed class EmptyScope : ICommandExecutionScope, IServiceProvider
    {
        public IServiceProvider Services => this;
        public object? GetService(Type serviceType) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
