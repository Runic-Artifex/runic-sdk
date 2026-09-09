using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Testing;

internal static partial class HostingAdapterTests
{
    private static async ValueTask CompletionTransport()
    {
        var catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        var adapter = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));
        foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
        {
            var hosted = new TestCommandConsole();
            var standalone = new TestCommandConsole();
            var decision = adapter.Classify(new(["completion", shell], transportOutputOptionName: "--runic-output"));
            AssertEqual(0, await adapter.PresentAsync(decision, hosted, CultureInfo.InvariantCulture, "completion"));
            AssertEqual(0, await new CommandApp(catalog)
            {
                Console = standalone, HandleCancelKeyPress = false,
                ParseSettings = new(transportOutputOptionName: "--runic-output"),
            }.RunAsync(["completion", shell]));
            AssertEqual(standalone.StandardOutput, hosted.StandardOutput);
            AssertTrue(hosted.StandardOutput.Contains("--runic-output", StringComparison.Ordinal));
            AssertTrue(!hosted.StandardOutput.Contains("--output", StringComparison.Ordinal));
        }
    }

    private static async ValueTask CatalogCompletion()
    {
        foreach (bool alias in new[] { false, true })
        {
            var factory = new TrackingHandlerFactory();
            var catalog = new CommandCatalogBuilder().Command<Options, Handler, Result>(alias ? "custom" : "completion", command =>
            {
                if (alias) command.Alias("completion");
                command.Argument("shell", "shell", CommandArity.ExactlyOne)
                    .BindWith(new DelegateBinder()).CreateHandlerWith(factory).Produces(ResultCodec.Instance);
            }).Build();
            var scopes = new TrackingScopeFactory();
            var adapter = new CommandLineHostingAdapter(catalog, new CommandExecutor(scopes));
            var decision = adapter.Classify(new(["completion", "bash"]));
            AssertEqual(HostedCommandLineDecisionKind.Invocation, decision.Kind);
            AssertTrue((await adapter.ExecuteAsync(new(decision, new SilentConsole(), CultureInfo.InvariantCulture, "owned", new CapturingSink()))).IsSuccess);
            AssertEqual(0, await new CommandApp(catalog) { ScopeFactory = scopes, Console = new SilentConsole(), HandleCancelKeyPress = false }.RunAsync(["completion", "bash"]));
            AssertEqual(2, factory.Created);
        }
    }

    private static ValueTask GlobalAliases()
    {
        CommandCatalog Build(string[] local) => new CommandCatalogBuilder()
            .GlobalOption("value", "--value", CommandArity.ExactlyOne, aliases: ["-v", "/v"])
            .Command<Options, Handler, Result>("run", command => command
                .Option("value", "--value", CommandArity.ExactlyOne, aliases: local)
                .BindWith(new DelegateBinder()).CreateHandlerWith(new TrackingHandlerFactory()).Produces(ResultCodec.Instance)).Build();
        AssertThrows<CommandCatalogValidationException>(() => Build(["-x"]));
        var catalog = Build(["/v", "-v"]); // Alias order is not semantically significant.
        foreach (string[] args in new string[][] { ["-v", "1", "run"], ["run", "-v", "1"] })
            AssertEqual(ParseOutcomeKind.Invocation, PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, ParseSettings.Default).Kind);
        return ValueTask.CompletedTask;
    }

    private static async ValueTask PresentationCancellation()
    {
        var catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        foreach (bool custom in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var app = new CommandApp(catalog)
            {
                Console = new SilentConsole(), HandleCancelKeyPress = false,
                HelpPresenter = new BlockingHelp(entered),
                PresentFrameworkRequest = custom ? async (_, _, token) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return 0;
                } : null,
            };
            Task<int> run = app.RunAsync(["--help"], cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            AssertEqual(DefaultExitCodePolicy.Instance.GetExitCode(CommandExitCategory.Cancelled), await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var adapter = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()));
        foreach (string[] args in new string[][] { ["--help"], ["--version"], ["unknown"], ["completion", "bash"] })
        {
            var console = new TestCommandConsole();
            int exit = await adapter.PresentAsync(adapter.Classify(new(args)), console, CultureInfo.InvariantCulture, "cancel", cancelled.Token);
            AssertEqual(DefaultExitCodePolicy.Instance.GetExitCode(CommandExitCategory.Cancelled), exit);
            AssertEqual("", console.StandardOutput);
            AssertEqual("", console.StandardError);
        }
    }

    private static async ValueTask PresentationFailures()
    {
        var catalog = CreateCatalog(new DelegateBinder(), new TrackingHandlerFactory());
        var failure = new InvalidOperationException("private-presentation-secret");
        int observed = 0;
        void Observe(Exception exception) { AssertTrue(ReferenceEquals(failure, exception)); observed++; throw new InvalidOperationException("observer-secret"); }
        foreach (int mode in new[] { 0, 1, 2 })
        {
            var console = new TestCommandConsole();
            var app = new CommandApp(catalog)
            {
                Console = console, HandleCancelKeyPress = false, ExceptionObserver = Observe,
                HelpPresenter = mode == 0 ? new FailingHelp(failure) : null,
                FormatHelp = mode == 1 ? _ => throw failure : null,
                PresentFrameworkRequest = mode == 2 ? (_, _, _) => throw failure : null,
            };
            AssertEqual(70, await app.RunAsync(["--help"]));
            AssertEqual("", console.StandardOutput);
            AssertEqual("", console.StandardError);
        }
        AssertEqual(3, observed);
        var adapter = new CommandLineHostingAdapter(catalog, new CommandExecutor(new TrackingScopeFactory()))
        {
            Presentation = new() { HelpPresenter = new FailingHelp(failure), ExceptionObserver = Observe },
        };
        AssertEqual(70, await adapter.PresentAsync(adapter.Classify(new(["--help"])), new SilentConsole(), CultureInfo.InvariantCulture, "fail"));
        AssertEqual(4, observed);
        foreach (string[] args in new string[][] { ["--version", "--output=json"], ["unknown"], ["completion", "bash"] })
        {
            var console = new FailingConsole(failure);
            AssertEqual(70, await new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false, ExceptionObserver = Observe }.RunAsync(args));
            AssertEqual(1, console.Writes);
            console = new FailingConsole(failure);
            AssertEqual(70, await adapter.PresentAsync(adapter.Classify(new(args)), console, CultureInfo.InvariantCulture, "write-fail"));
            AssertEqual(1, console.Writes);
        }
        AssertEqual(10, observed);
    }

    private sealed class BlockingHelp(TaskCompletionSource entered) : ICommandHelpPresenter
    {
        public async ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName, ICommandConsole console, CancellationToken cancellationToken)
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
    private sealed class FailingHelp(Exception failure) : ICommandHelpPresenter
    {
        public ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName, ICommandConsole console, CancellationToken cancellationToken) => throw failure;
    }
    private sealed class FailingConsole(Exception failure) : ICommandConsole
    {
        public int Writes { get; private set; }
        public bool IsInteractive => false;
        public bool IsInputRedirected => true;
        public bool IsOutputRedirected => true;
        public bool IsErrorRedirected => true;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { Writes++; throw failure; }
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { Writes++; throw failure; }
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) { Writes++; throw failure; }
    }
}
