using System.Globalization;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;

// The application calls this with its lifetime token. The adapter never installs
// process signal handlers, starts a host, or disposes application-owned services.
internal static class HostedExample
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken applicationStopping)
    {
        var services = new ApplicationServices("Hello from application services");
        var console = new SpectreCommandConsole();
        var adapter = new CommandLineHostingAdapter(GeneratedCommandCatalog.Create(), new CommandExecutor(new CommandScopes(services)))
        {
            Presentation = new() { Name = "hello", Version = "1.0.0", HelpPresenter = new SpectreHelpPresenter(), ExceptionObserver = services.RecordException },
        };
        var launch = new HostedCommandLineLaunchInput(args,
            outputEnvironmentValue: Environment.GetEnvironmentVariable("RUNIC_COMMANDLINE_OUTPUT"),
            emptyInputFallback: EmptyInputFallback.UserInterface)
        {
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["HELLO_ENV"] = Environment.GetEnvironmentVariable("HELLO_ENV"),
            },
        };
        var decision = adapter.Classify(launch);
        if (decision.Kind == HostedCommandLineDecisionKind.UserInterface)
        {
            // A desktop application calls its existing UI launch method here.
            // This console example only demonstrates the launch decision.
            await console.WriteOutAsync("Application selected its UI launch path.\n".AsMemory(), applicationStopping);
            return 0;
        }
        string correlationId = Guid.NewGuid().ToString("N");
        if (!decision.CanExecute)
            return await adapter.PresentAsync(decision, console, CultureInfo.CurrentCulture, correlationId, applicationStopping);
        var result = await adapter.ExecuteAsync(new(decision, console, CultureInfo.CurrentCulture, correlationId, new CommandOutputDispatcher())
        {
            ExceptionObserver = services.RecordException,
        }, applicationStopping);
        return result.ExitCode;
    }

    internal static ICommandExecutionScopeFactory CreateCommandScopes() =>
        new CommandScopes(new ApplicationServices("Hello from application services"));

    [Command("application info", Description = "Read services supplied by the application host.")]
    internal static string Info([FromServices] ApplicationServices services) => services.Greeting;

    internal sealed class ApplicationServices(string greeting)
    {
        internal string Greeting { get; } = greeting;
        internal Exception? LastException { get; private set; }
        internal void RecordException(Exception exception) => LastException = exception;
    }

    private sealed class CommandScopes(ApplicationServices services) : ICommandExecutionScopeFactory
    {
        public ICommandExecutionScope CreateScope() => new CommandScope(services);
    }

    private sealed class CommandScope(ApplicationServices services) : ICommandExecutionScope, IServiceProvider
    {
        public IServiceProvider Services => this;
        public object? GetService(Type type) => type == typeof(ApplicationServices) ? services : null;
        // Only invocation-owned resources belong here, not the application's services.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
