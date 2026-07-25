using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.CommandLine;
using WebUIToolkit.DependencyNotices.Runtime;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.WebUi;
using WebUIToolkit.MVVM;
using WebUIToolkit.MVVM.Flow;
using WebUIToolkit.MVVM.Navigation;
using WebUIToolkit.MVVM.Operations;
using WebUIToolkit.TextResources;

namespace WebUIToolkit.ReferenceApplication;

internal static partial class Program
{
    private const int CountMember = 1;
    private const int IncrementCommand = 2;
    private static readonly MvvmBindingVocabulary Vocabulary = new(
    [
        new MvvmBindingMember(CountMember, MvvmBindingMemberKind.Property, "Count"),
        new MvvmBindingMember(IncrementCommand, MvvmBindingMemberKind.Command, "Increment"),
    ]);

    private static readonly Scenario[] Scenarios =
    [
        new("hosting", RunHostingAsync),
        new("mvvm-reconnect", RunMvvmAsync),
        new("flow-navigation", RunFlowAsync),
        new("text-resources", RunTextResourcesAsync),
        new("command-line", RunCommandLineAsync),
        new("dependency-notices", RunDependencyNoticesAsync),
        new("webui-assets", RunWebUiAsync),
    ];

    public static async Task<int> Main()
    {
        ScenarioResult[] results = await Task.WhenAll(
            Scenarios.Select(static scenario => RunScenarioAsync(scenario))).ConfigureAwait(false);
        foreach (ScenarioResult result in results)
        {
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"G7-REFERENCE: {result.Name}={result.Status}"));
            if (result.Error is not null)
            {
                Console.Error.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"G7-REFERENCE-ERROR: {result.Name} {result.Error}"));
            }
        }

        bool passed = results.All(static result => result.Error is null);
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"G7-REFERENCE: {(passed ? "passed" : "failed")} packageOnly=true protocol={MvvmProtocol.Identity} scenarios={results.Length}"));
        return passed ? 0 : 1;
    }

    private static async Task<ScenarioResult> RunScenarioAsync(Scenario scenario)
    {
        try
        {
            await scenario.Run().ConfigureAwait(false);
            return new ScenarioResult(scenario.Name, "passed", null);
        }
        catch (Exception exception)
        {
            return new ScenarioResult(scenario.Name, "failed", exception.GetType().Name);
        }
    }

    private static async Task RunHostingAsync()
    {
        var builder = new GenericHostWebUIToolkitApplicationBuilder();
        builder.DisableLifecycleLogging();
        builder.Application.AddModeRunner(new ExitRunner());
        await using WebUIToolkitApplication application = builder.Build();
        ApplicationRunResult result = await application
            .RunAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Require(result.IsSuccess && result.ExitCode == 0, "The packaged host did not complete cleanly.");
    }

    private static async Task RunMvvmAsync()
    {
        var state = new CounterState();
        int cleanupCalls = 0;
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("reference.counter"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(
                CreateAdapter(state, () => cleanupCalls++))));

        await using IMvvmSessionFactory factory = registry.Build();
        IMvvmSession session = await factory
            .OpenAsync(new MvvmContract("reference.counter"))
            .ConfigureAwait(false);
        try
        {
            MvvmResponse initial = await session
                .DispatchAsync(new MvvmSnapshotRequest(NewRequestId()))
                .ConfigureAwait(false);
            MvvmResponse mutation = await session.DispatchAsync(
                new MvvmMutationRequest(
                    NewRequestId(),
                    MvvmMutationKind.ExecuteCommand,
                    baseRevision: 0,
                    memberId: IncrementCommand,
                    payload: MvvmValue.Null)).ConfigureAwait(false);
            MvvmResponse acknowledgement = await session
                .DispatchAsync(new MvvmAcknowledgeRequest(NewRequestId(), revision: 1))
                .ConfigureAwait(false);
            MvvmResponse reconnect = await session
                .DispatchAsync(new MvvmSnapshotRequest(NewRequestId()))
                .ConfigureAwait(false);

            Require(SnapshotHasCount(initial, 0, 0), "Initial MVVM snapshot was invalid.");
            Require(mutation.Succeeded && mutation.Revision == 1, "MVVM mutation was not committed.");
            Require(acknowledgement.Succeeded && session.AcknowledgedRevision == 1, "MVVM acknowledgement failed.");
            Require(SnapshotHasCount(reconnect, 1, 1), "Reconnect did not replace state from the authoritative snapshot.");
            Require(session.Authorizes(session.CapabilityToken), "Session capability authorization failed.");
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        Require(cleanupCalls == 1, "MVVM subscriptions were not disposed exactly once.");
    }

    private static async Task RunFlowAsync()
    {
        RegionKey region = new("reference.main");
        RouteKey home = new("reference.home");
        RouteKey details = new("reference.details");
        NavigationRegistry registry = new NavigationRegistryBuilder()
            .AddPage<ReferenceViewModel>(
                home,
                new ViewContract("reference.home"),
                static _ => ValueTask.FromResult(
                    new NavigationRouteContent(new ReferenceViewModel("home"), new ReferenceScope())))
            .AddPage<ReferenceDetailsViewModel>(
                details,
                new ViewContract("reference.details"),
                static _ => ValueTask.FromResult(
                    new NavigationRouteContent(new ReferenceDetailsViewModel(42), new ReferenceScope())))
            .AddRegion(new NavigationRegionRegistration(region, home, requireContent: true))
            .Build();

        var presenter = new ReferenceNavigationPresenter();
        await using var navigation = new NavigationService(registry, presenter);
        await navigation.StartAsync().ConfigureAwait(false);
        NavigationResult pushed = await navigation
            .NavigateAsync<ReferenceDetailsViewModel>(region)
            .ConfigureAwait(false);
        NavigationResult backed = await navigation.BackAsync(region).ConfigureAwait(false);
        NavigationSnapshot snapshot = navigation.GetSnapshot(region);
        await navigation.ShutdownAsync().ConfigureAwait(false);

        var runner = new OperationRunner();
        OperationOutcome<int> operation = await runner.TryRunAsync(
            new OperationRequest(new OperationKey("reference.load"), CorrelationId: "reference"),
            static (context, _) =>
            {
                context.Report(new OperationProgress(1, "complete"));
                return ValueTask.FromResult(42);
            }).ConfigureAwait(false);

        Require(pushed.Kind == NavigationResultKind.Navigated, "Flow details navigation failed.");
        Require(backed.Kind == NavigationResultKind.Navigated, "Flow back navigation failed.");
        Require(snapshot.Current?.Route == home, "Flow did not return to the home route.");
        Require(presenter.PresentationCount >= 3, "Flow presentation lifecycle was incomplete.");
        Require(operation == OperationOutcome<int>.Succeeded(42), "Flow operation did not complete.");
    }

    private static async Task RunTextResourcesAsync()
    {
        var catalog = new CompiledTextResourceCatalog(
            "reference",
            "en",
            [new CompiledTextResourceDefinition(
                "greeting",
                [new TextResourcePlaceholderDescriptor(
                    "name",
                    TextArgumentType.String,
                    TextArgumentFormat.None)])],
            [
                new CompiledTextResourceLocale("de", "en", [new CompiledTextResourceValue(0, "Hallo {name}")]),
                new CompiledTextResourceLocale("en", null, [new CompiledTextResourceValue(0, "Hello {name}")]),
            ]);
        var provider = new CompiledTextResourceProvider(catalog);
        ITextResourceSnapshot initial = await provider.GetSnapshotAsync("en").ConfigureAwait(false);
        var manager = new TextResourceManager(provider, initial);
        var key = new TextResourceKey("reference", 0, "greeting");

        string english = manager.Current.Format(key, [new TextArgument("name", "Ada")]);
        await manager.SetLocaleAsync("de").ConfigureAwait(false);
        string german = manager.Current.Format(key, [new TextArgument("name", "Ada")]);
        Require(english == "Hello Ada" && german == "Hallo Ada", "Typed text-resource locale transition failed.");
    }

    private static Task RunCommandLineAsync()
    {
        CommandCatalog catalog = new CommandCatalogBuilder()
            .Command<ProbeOptions, ProbeHandler, ProbeResult>(
                "probe",
                command => command
                    .Option("label", "--label", CommandArity.ExactlyOne)
                    .BindWith(ProbeOptionsBinder.Instance)
                    .CreateHandlerWith(ProbeHandlerFactory.Instance)
                    .Produces(ProbeResultCodec.Instance))
            .Build();
        ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            ["probe", "--label", "release"],
            new ParseSettings(null));
        Require(parse.Kind == ParseOutcomeKind.Invocation, "The portable command parser rejected a valid invocation.");
        Require(parse.Invocation?.Options.Single().Values.Single() == "release", "The command option was not preserved.");
        return Task.CompletedTask;
    }

    private static Task RunDependencyNoticesAsync()
    {
        const string json = """
            {"schemaVersion":2,"artifactName":"reference-application","artifactVersion":"1.0.0","dependencies":[],"sbom":null,"diagnostics":[]}
            """;
        NoticeDocument document = NoticeDocumentLoader.Load(Encoding.UTF8.GetBytes(json).AsSpan());
        Require(document.ArtifactName == "reference-application", "The notice document identity drifted.");
        Require(document.Dependencies.Count == 0, "The empty reference notice document gained dependencies.");
        return Task.CompletedTask;
    }

    private static async Task RunWebUiAsync()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var entryPoint = new FrontendAsset("index.html", "text/html", 5, digest, isEntryPoint: true);
        var provider = new ReferenceAssetProvider(new ReferenceAssetManifest([entryPoint]));
        var endpoint = new FrontendAssetEndpoint(provider, new Uri("app://reference/"));
        await using FrontendAssetResponse response = await endpoint
            .OpenAsync(new FrontendAssetRequest("index.html"), CancellationToken.None)
            .ConfigureAwait(false);
        using var reader = new StreamReader(response.Content, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false);
        Require(endpoint.EntryPoint.AbsoluteUri == "app://reference/index.html", "The WebUi entry point was invalid.");
        Require(body == "index", "The WebUi asset body was invalid.");
    }

    private static IMvvmBindingAdapter CreateAdapter(CounterState state, Action cleanup)
    {
        return new MvvmBindingAdapterBuilder(
            _ => ValueTask.FromResult(CreateSnapshot(state.Count)),
            Vocabulary)
            .BindCommand(
                IncrementCommand,
                (_, _) =>
                {
                    state.Count++;
                    return ValueTask.FromResult(
                        new MvvmProjectionPatchBuilder(Vocabulary)
                            .Property(CountMember, MvvmValue.From((long)state.Count))
                            .Command(IncrementCommand, canExecute: true, isExecuting: false)
                            .Success(MvvmValue.From((long)state.Count)));
                },
                diagnosticName: "Increment")
            .OnDispose(() =>
            {
                cleanup();
                return ValueTask.CompletedTask;
            })
            .Build();
    }

    private static MvvmSnapshot CreateSnapshot(int count) =>
        new MvvmProjectionSnapshotBuilder(Vocabulary)
            .AddProperty(CountMember, MvvmValue.From((long)count))
            .AddCommand(IncrementCommand, canExecute: true, isExecuting: false)
            .Build();

    private static bool SnapshotHasCount(MvvmResponse response, long revision, long count)
    {
        if (!response.Succeeded ||
            response.Revision != revision ||
            response.Payload is not JsonElement payload ||
            !payload.TryGetProperty("members", out JsonElement members) ||
            members.GetArrayLength() != 2)
        {
            return false;
        }

        JsonElement property = members[0];
        return property.GetProperty("type").GetString() == "property" &&
            property.GetProperty("member").GetInt32() == CountMember &&
            property.GetProperty("value").GetInt64() == count;
    }

    private static MvvmRequestId NewRequestId() => new(Guid.NewGuid());

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record Scenario(string Name, Func<Task> Run);

    private sealed record ScenarioResult(string Name, string Status, string? Error);

    private sealed class CounterState
    {
        internal int Count { get; set; }
    }

    private sealed record ReferenceViewModel(string State);

    private sealed record ReferenceDetailsViewModel(int Id);

    private sealed class ReferenceScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ReferenceNavigationPresenter : INavigationRegionPresenter
    {
        public int PresentationCount { get; private set; }

        public ValueTask<IFlowPresentationLease> PresentAsync(
            RegionKey region,
            FlowContentDescriptor content,
            NavigationPresentationContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PresentationCount++;
            return ValueTask.FromResult<IFlowPresentationLease>(new ReferenceLease());
        }

        public ValueTask ClearAsync(RegionKey region, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReferenceLease : IFlowPresentationLease
    {
        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ExitRunner : IApplicationModeRunner
    {
        public LaunchKind Kind => LaunchKind.UserInterface;

        public Task<ApplicationRunResult> RunAsync(
            LaunchDecision decision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ApplicationRunResult.FromExitCode(0));
        }
    }

    private sealed class ReferenceAssetManifest(IReadOnlyList<FrontendAsset> assets)
        : IFrontendAssetManifest
    {
        public string ManifestVersion => "1";

        public IReadOnlyList<FrontendAsset> Assets { get; } = assets;
    }

    private sealed class ReferenceAssetProvider(ReferenceAssetManifest manifest)
        : IFrontendAssetProvider
    {
        public IFrontendAssetManifest Manifest { get; } = manifest;

        public ValueTask ValidateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<Stream> OpenReadAsync(
            string relativePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(relativePath == "index.html", "The WebUi provider received an unknown path.");
            return ValueTask.FromResult<Stream>(
                new MemoryStream("index"u8.ToArray(), writable: false));
        }
    }

    private sealed record ProbeOptions(string Label);

    private sealed record ProbeResult(string Label);

    private sealed class ProbeOptionsBinder : ICommandOptionsBinder<ProbeOptions>
    {
        internal static ProbeOptionsBinder Instance { get; } = new();

        public ValueTask<CommandOutcome<ProbeOptions>> BindAsync(
            ParsedInvocation invocation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CommandOutcome.Success(new ProbeOptions(
                invocation.Options.Single().Values.Single())));
        }
    }

    private sealed class ProbeHandler : ICommandHandler<ProbeOptions, ProbeResult>
    {
        public ValueTask<CommandOutcome<ProbeResult>> ExecuteAsync(
            ProbeOptions options,
            CommandExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                CommandOutcome.Success(new ProbeResult(options.Label)));
        }
    }

    private sealed class ProbeHandlerFactory : ICommandHandlerFactory<ProbeHandler>
    {
        internal static ProbeHandlerFactory Instance { get; } = new();

        public ProbeHandler Create(IServiceProvider services)
        {
            ArgumentNullException.ThrowIfNull(services);
            return new ProbeHandler();
        }
    }

    private sealed class ProbeResultCodec : ICommandResultCodec<ProbeResult>
    {
        internal static ProbeResultCodec Instance { get; } = new();

        public string PayloadType => "webuitoolkit.reference.probe/1";

        public JsonTypeInfo<ProbeResult> TypeInfo => ReferenceJsonContext.Default.ProbeResult;

        public ValueTask WriteHumanAsync(
            ProbeResult value,
            ICommandConsole console,
            CultureInfo culture,
            CancellationToken cancellationToken) =>
            console.WriteOutAsync(value.Label.AsMemory(), cancellationToken);
    }

    [JsonSerializable(typeof(ProbeResult))]
    private sealed partial class ReferenceJsonContext : JsonSerializerContext
    {
    }
}
