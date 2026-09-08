using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application;
using Runic.Application.Bridge;
using Runic.Application.CsWebUi;
using Runic.Application.Desktop;
using Runic.Assets;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;
using Runic.Application.Platform;
using Runic.Application.Platform.Desktop;

internal static class LiveHostTests
{
    internal static async Task RunAsync(bool csWebUi)
    {
        var services = new ServiceCollection();
        Feature? feature = null;
        services.AddRunicPlatform();
        services.AddScoped(provider => new Feature(provider.GetRequiredService<PresentationLifetime>()));
        services.AddScoped<IApplicationBridgeDispatcher>(provider => new Dispatcher(feature = provider.GetRequiredService<Feature>()));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        ApplicationBridgeSession? session = null;
        ApplicationBridgeSession Create() => session = ApplicationBridgeSessionFactory.Create(provider);
        await using IApplicationHost host = csWebUi
            ? new CsWebUiApplicationHost(new() { Assets = new TestAssets(), OpenWindow = false, CreateBridgeSession = Create })
            : new DesktopApplicationHost(new() { OpenWindow = false, CreateBridgeSession = Create });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await host.StartAsync(new("platform.live", "0.0.0", "test", []), ReadOnlyMemory<string>.Empty, provider, deadline.Token);
        var live = feature ?? throw new InvalidOperationException("The live bridge did not resolve its scoped feature.");
        var bridge = session ?? throw new InvalidOperationException("The live host did not create its session.");
        // Drive the same real session owned by the live transport, not a second scope.
        await bridge.DispatchAsync(Frame(bridge, "initialize", 0), deadline.Token);
        await bridge.DispatchAsync(Frame(bridge, "initialize", 1), deadline.Token);
        Check(!live.Lifetime.IsClosing && live.Snapshots == 2, "Reconnect stopped or replaced the scoped service.");
        var pending = bridge.DispatchAsync(Frame(bridge, "dispatch", 1), deadline.Token).AsTask();
        await live.Entered.Task.WaitAsync(deadline.Token);
        Task first = host.StopAsync(CancellationToken.None).AsTask();
        Task second = host.StopAsync(CancellationToken.None).AsTask();
        await live.Releasing.Task.WaitAsync(deadline.Token);
        Check(!first.IsCompleted && !second.IsCompleted, "Concurrent host stop abandoned native release.");
        live.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(deadline.Token);
        await pending.WaitAsync(deadline.Token);
        Check(live.Disposals == 1 && live.Lifetime.IsClosing, "The feature scope was not disposed exactly once.");
        Check(live.ReleaseSawShutdown, "The lifetime hook ran too late to unblock its command.");
        Console.WriteLine($"PASS live {(csWebUi ? "CS-WebUI" : "Desktop")}: session scope, reconnect, blocked operation and joined stop.");
    }

    private static BridgeClientEnvelope Frame(ApplicationBridgeSession session, string kind, long epoch) => new()
    {
        Protocol = "runic.platform.test", Version = 1, ContractFingerprint = new('a', 64), ConnectionEpoch = epoch,
        Kind = kind, CommandId = Guid.NewGuid(), SessionId = kind == "initialize" ? null : session.Id.Value,
        ExpectedRevision = kind == "initialize" ? null : 0, Payload = TestJson.Empty,
    };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Feature(PresentationLifetime lifetime) : IAsyncDisposable
    {
        internal PresentationLifetime Lifetime => lifetime;
        internal int Snapshots, Disposals;
        internal bool ReleaseSawShutdown;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Releasing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task WaitForOwnerShutdown()
        {
            using var operation = lifetime.TryBeginOperation() ?? throw new InvalidOperationException("Owner closed.");
            Entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Shutdown); }
            catch (OperationCanceledException) when (lifetime.IsClosing)
            {
                ReleaseSawShutdown = true; Releasing.SetResult(); await Release.Task;
            }
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Dispatcher(Feature feature) : IApplicationBridgeDispatcher
    {
        public string ProtocolIdentity => "runic.platform.test";
        public int ProtocolVersion => 1;
        public string ManifestFingerprint => new('a', 64);
        public ValueTask<JsonElement> GetSnapshotAsync(BridgeSnapshotContext context, CancellationToken cancellationToken)
        { feature.Snapshots++; return ValueTask.FromResult(TestJson.Zero); }
        public async ValueTask<BridgeDispatchResult> DispatchAsync(JsonElement command, BridgeCommandContext context, CancellationToken cancellationToken)
        { await feature.WaitForOwnerShutdown(); return new(TestJson.Zero); }
    }
    internal sealed class TestAssets : IAssetSource
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("<!doctype html><title>Runic platform conformance</title>");
        public AssetManifest Manifest { get; } = new([new("index.html", "text/html", Content.Length, Convert.ToHexString(SHA256.HashData(Content)), true)]);
        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(new MemoryStream(Content, writable: false)); }
    }
}
