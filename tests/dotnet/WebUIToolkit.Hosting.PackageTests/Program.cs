using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.Hosting;
using WebUIToolkit.Hosting.Build;

return await PackageConsumerScenarios.RunAsync().ConfigureAwait(false);

internal static class PackageConsumerScenarios
{
    private static readonly (string Name, Func<Task> Run)[] Scenarios =
    [
        ("package classifier is deterministic", ClassifierIsDeterministicAsync),
        ("package routing selects one fake runner", RoutingSelectsOneRunnerAsync),
        ("package builder composes and runs fakes", BuilderComposesAndRunsAsync),
        ("package lifecycle events remain sanitized", EventsRemainSanitizedAsync),
        ("package asset contracts work with a fake provider", AssetContractsWorkAsync),
        ("package build kernel emits a deterministic manifest", BuildKernelIsDeterministicAsync),
        ("package browser contracts work with a fake host", BrowserContractsWorkAsync),
    ];

    internal static async Task<int> RunAsync()
    {
        Console.WriteLine($"1..{Scenarios.Length}");
        int failures = 0;

        for (int index = 0; index < Scenarios.Length; index++)
        {
            (string name, Func<Task> scenario) = Scenarios[index];
            try
            {
                await scenario().ConfigureAwait(false);
                Console.WriteLine($"ok {index + 1} - {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.WriteLine($"not ok {index + 1} - {name}: {exception.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static Task ClassifierIsDeterministicAsync()
    {
        var resolver = new DefaultLaunchIntentResolver();
        AssertEqual(LaunchKind.UserInterface, resolver.Resolve(Array.Empty<string>()).Kind, "empty launch");
        AssertEqual(LaunchKind.Help, resolver.Resolve(["--help"]).Kind, "help launch");
        AssertEqual(LaunchKind.Version, resolver.Resolve(["--version"]).Kind, "version launch");

        LaunchDecision command = resolver.Resolve(["sync", "--dry-run"]);
        AssertEqual(LaunchKind.Command, command.Kind, "command launch");
        AssertEqual("sync", command.CommandName, "command name");

        LaunchDecision invalid = resolver.Resolve(["--help", "extra"]);
        AssertEqual(LaunchKind.Invalid, invalid.Kind, "ambiguous launch");
        Assert(
            invalid.Diagnostic is not null && !invalid.Diagnostic.Contains("extra", StringComparison.Ordinal),
            "classification diagnostic must be safe");
        return Task.CompletedTask;
    }

    private static async Task RoutingSelectsOneRunnerAsync()
    {
        var ui = new FakeModeRunner(LaunchKind.UserInterface);
        var command = new FakeModeRunner(LaunchKind.Command);
        var routes = new ApplicationModeRouteTable([ui, command]);

        ApplicationModeRouteSelection selection = routes.SelectRunner(LaunchKind.Command);
        Assert(selection.IsSuccess, "command route must succeed");
        Assert(ReferenceEquals(command, selection.Runner), "route must return the registered fake");

        ApplicationRunResult result = await selection.Runner!
            .RunAsync(new LaunchDecision(LaunchKind.Command, ["sync"], "sync"), CancellationToken.None)
            .ConfigureAwait(false);
        Assert(result.IsSuccess, "fake runner result must flow through the package contract");

        ApplicationModeRouteSelection missing = routes.SelectRunner(LaunchKind.Help);
        Assert(!missing.IsSuccess, "missing route must fail");
        AssertEqual(ApplicationFailureCodes.RunnerSelection, missing.Error!.Code, "route failure code");
    }

    private static Task EventsRemainSanitizedAsync()
    {
        var sink = new RecordingEventSink();
        var failure = new ApplicationFailureEvent(
            1,
            DateTimeOffset.UnixEpoch,
            true,
            ApplicationFailureCategory.UserInterface,
            ApplicationFailureCodes.RunnerFailure,
            false);

        sink.Publish(failure);
        IReadOnlyList<ApplicationLifecycleEvent> snapshot = sink.Snapshot;
        AssertEqual(ApplicationLifecycleEventIds.PrimaryFailure, snapshot[0].EventId, "event id");
        AssertEqual(1L, snapshot[0].Sequence, "event sequence");
        AssertEqual(ApplicationFailureCodes.RunnerFailure, failure.FailureCode, "safe failure code");
        Assert(
            failure.GetType().GetProperties().All(property => property.Name != "Exception"),
            "event contract must not expose an exception");
        return Task.CompletedTask;
    }

    private static async Task BuilderComposesAndRunsAsync()
    {
        var host = new FakeApplicationHost();
        var runner = new FakeModeRunner(LaunchKind.UserInterface);
        var events = new RecordingEventSink();

        await using WebUIToolkitApplication application = new WebUIToolkitApplicationBuilder()
            .UseHost(host)
            .AddModeRunner(runner)
            .UseLifecycleEventSink(events)
            .Build();

        ApplicationRunResult result = await application.RunAsync(CancellationToken.None)
            .ConfigureAwait(false);
        await events.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        IReadOnlyList<ApplicationLifecycleEvent> snapshot = events.Snapshot;
        Assert(result.IsSuccess, "built application must return the fake runner result");
        AssertEqual(1, host.StartCount, "host start count");
        AssertEqual(1, host.StopCount, "host stop count");
        Assert(
            snapshot.Any(item => item.EventId == ApplicationLifecycleEventIds.LaunchSelected),
            "builder event sink must receive launch selection");
        Assert(
            snapshot.Any(item => item.EventId == ApplicationLifecycleEventIds.Completion),
            "builder event sink must receive completion");
    }

    private static async Task AssetContractsWorkAsync()
    {
        const string digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var entryPoint = new FrontendAsset("app/index.html", "text/html", 5, digest, isEntryPoint: true);
        var provider = new FakeAssetProvider(new FakeAssetManifest("1", [entryPoint]));

        await provider.ValidateAsync(CancellationToken.None).ConfigureAwait(false);
        await using Stream stream = await provider
            .OpenReadAsync("app/index.html", CancellationToken.None)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        AssertEqual("index", await reader.ReadToEndAsync(CancellationToken.None).ConfigureAwait(false), "asset body");
        AssertEqual("app/index.html", provider.Manifest.Assets[0].RelativePath, "normalized asset path");
    }

    private static async Task BrowserContractsWorkAsync()
    {
        var factory = new FakeBrowserHostFactory();
        await using IBrowserHost host = await factory
            .CreateAsync(new BrowserHostOptions("package-consumer"), CancellationToken.None)
            .ConfigureAwait(false);
        await host.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await using IBrowserWindow window = await host
            .CreateWindowAsync(new BrowserWindowOptions("main", "Package consumer"), CancellationToken.None)
            .ConfigureAwait(false);

        await window.NavigateAsync(new Uri("https://localhost/index.html"), CancellationToken.None)
            .ConfigureAwait(false);
        await window.ShowAsync(CancellationToken.None).ConfigureAwait(false);
        await window.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        await window.WaitForCloseAsync(CancellationToken.None).ConfigureAwait(false);
        Assert(host.Dispatcher.CheckAccess(), "fake dispatcher must be callable through the package contract");
    }

    private static Task BuildKernelIsDeterministicAsync()
    {
        var builder = new FrontendAssetManifestBuilder();
        FrontendAssetManifest manifest = builder.Build(
        [
            new FrontendAssetBuildItem("scripts/app.js", "script"u8.ToArray()),
            new FrontendAssetBuildItem("index.html", "index"u8.ToArray(), isEntryPoint: true),
        ]);

        AssertEqual("index.html", manifest.Assets[0].RelativePath, "manifest ordinal ordering");
        AssertEqual("scripts/app.js", manifest.Assets[1].RelativePath, "manifest ordinal ordering");
        string first = FrontendAssetManifestJson.Serialize(manifest);
        string second = FrontendAssetManifestJson.Serialize(manifest);
        AssertEqual(first, second, "canonical manifest JSON");
        Assert(first.Contains("webuitoolkit.frontend-assets/1", StringComparison.Ordinal), "manifest version");
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message}: expected '{expected}', actual '{actual}'");
        }
    }
}

internal sealed class FakeModeRunner(LaunchKind kind) : IApplicationModeRunner
{
    public LaunchKind Kind { get; } = kind;

    public Task<ApplicationRunResult> RunAsync(
        LaunchDecision decision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ApplicationRunResult.FromExitCode(0));
    }
}

internal sealed class RecordingEventSink : IApplicationLifecycleEventSink
{
    private readonly object _sync = new();
    private readonly List<ApplicationLifecycleEvent> _events = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<ApplicationLifecycleEvent> Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _events.ToArray();
            }
        }
    }

    public Task Completion => _completion.Task;

    public void Publish(ApplicationLifecycleEvent lifecycleEvent)
    {
        lock (_sync)
        {
            _events.Add(lifecycleEvent);
        }

        if (lifecycleEvent is ApplicationCompletionEvent)
        {
            _completion.TrySetResult();
        }
    }
}

internal sealed class FakeApplicationHost : IApplicationHost
{
    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeAssetManifest(
    string manifestVersion,
    IReadOnlyList<FrontendAsset> assets) : IFrontendAssetManifest
{
    public string ManifestVersion { get; } = manifestVersion;

    public IReadOnlyList<FrontendAsset> Assets { get; } = assets;
}

internal sealed class FakeAssetProvider(FakeAssetManifest manifest) : IFrontendAssetProvider
{
    public IFrontendAssetManifest Manifest { get; } = manifest;

    public ValueTask ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Manifest.Assets.Count != 1 || !Manifest.Assets[0].IsEntryPoint)
        {
            throw new InvalidOperationException("The fake manifest must contain one entry point.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(relativePath, Manifest.Assets[0].RelativePath))
        {
            throw new FileNotFoundException("Asset is not declared by the fake manifest.", relativePath);
        }

        Stream stream = new MemoryStream("index"u8.ToArray(), writable: false);
        return ValueTask.FromResult(stream);
    }
}

internal sealed class FakeBrowserHostFactory : IBrowserHostFactory
{
    public ValueTask<IBrowserHost> CreateAsync(
        BrowserHostOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IBrowserHost>(new FakeBrowserHost());
    }
}

internal sealed class FakeBrowserHost : IBrowserHost
{
    public IUiDispatcher Dispatcher { get; } = new InlineDispatcher();

    public ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<IBrowserWindow> CreateWindowAsync(
        BrowserWindowOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IBrowserWindow>(new FakeBrowserWindow());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeBrowserWindow : IBrowserWindow
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler? CloseRequested;

    public ValueTask NavigateAsync(Uri entryPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public Task WaitForCloseAsync(CancellationToken cancellationToken) => _closed.Task.WaitAsync(cancellationToken);

    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _closed.TrySetResult();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _closed.TrySetResult();
        CloseRequested = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public ValueTask InvokeAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken) => callback(cancellationToken);

    public ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> callback,
        CancellationToken cancellationToken) => callback(cancellationToken);
}
