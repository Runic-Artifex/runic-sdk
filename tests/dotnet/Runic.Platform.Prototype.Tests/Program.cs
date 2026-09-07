using Microsoft.Extensions.DependencyInjection;
using Runic.Application;
using Runic.Application.CsWebUi;
using Runic.Application.Desktop;
using Runic.Assets;
using Runic.Platform.Prototype;

if (args.Contains("--native-select", StringComparer.Ordinal)) return NativeHostTests.Run(manualSelection: true);
if (args.Contains("--native", StringComparer.Ordinal)) return NativeHostTests.Run();
if (args.Contains("--live-cswebui", StringComparer.Ordinal))
{
    await LiveHostTests.RunAsync(csWebUi: true);
    return 0;
}
return await Conformance.RunAsync();

internal static class Conformance
{
    internal static async Task<int> RunAsync()
    {
        (string Name, Func<Task> Run)[] tests =
        [
            ("LIVE: Desktop session-owned scopes and shutdown", () => LiveHostTests.RunAsync(csWebUi: false)),
            ("ACCESS: acquisition failure and selected-file permission limits", NativeAccessTests.RunAsync),
            ("FILE: concrete stream access, staging, conflict and commit cancellation", FileLeaseTests.RunAsync),
            ("CAP-01/02/03/04: readiness, immutable snapshots and explicit ownership", Capabilities),
            ("PICK-01/02/04: dismissal, pre-cancellation and one picker per owner", PickerAdmission),
            ("PICK-03: late selection after caller cancellation releases access", LateCancellation),
            ("PICK-05: owner shutdown waits for late access release", OwnerShutdown),
            ("PICK-05: failed access release remains a shutdown failure", CleanupFailure),
            ("PICK: provider failure releases admission for the next request", ProviderFailure),
            ("THREAD-01/02/03: worker dispatch, inline dispatch and queued cancellation", Dispatch),
            ("THREAD-04: queued rejection and running callback drain", DispatchShutdown),
            ("COMPOSE: identical scoped feature injection with Desktop and CS-WebUI", Composition),
        ];
        int failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine($"PASS {test.Name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {test.Name}\n{error}"); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} internal platform prototype scenarios passed; no native provider certification.");
        return failures == 0 ? 0 : 1;
    }

    private static async Task Capabilities()
    {
        await using var lifetime = new PresentationLifetime();
        var absent = new PresentationFiles(lifetime);
        Check(await absent.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured });
        var backend = new ControlledBackend();
        var files = new PresentationFiles(lifetime, backend);
        var before = files.GetSnapshot();
        Check(before.Statuses["platform.files.open"] is CapabilityStatus.Unavailable { Reason: UnavailableReason.OwnerUnavailable });
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerUnavailable });
        var unowned = files.OpenFileAsync(new(OwnerPolicy.AllowUnowned)).AsTask();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Dismissed());
        Check(await unowned is PickerResult<IReadFileLease>.Dismissed);
        Check(files.GetSnapshot().Statuses["platform.dialogs.owned"] is CapabilityStatus.Unavailable);
        lifetime.AttachTestOwner();
        Check(files.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Available);
        Check(before.Statuses["platform.files.open"] is CapabilityStatus.Unavailable);
        backend.IsAvailable = false;
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.BackendUnavailable });
        Check(backend.OpenCalls == 1);
        await lifetime.DisposeAsync();
        Check(files.GetSnapshot().Generation == before.Generation);
        Check(files.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Unavailable { Reason: UnavailableReason.OwnerClosed });
    }

    private static async Task PickerAdmission()
    {
        await using var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Throws<OperationCanceledException>(() => files.OpenFileAsync(new(), cancelled.Token).AsTask());
        Check(backend.OpenCalls == 0);
        var first = files.OpenFileAsync(new()).AsTask();
        var anotherFacade = new PresentationFiles(owner, backend);
        Check(await anotherFacade.SaveFileAsync(new("contact.json")) is PickerResult<ISaveFileLease>.Failed { Code: FailureCode.ResourceBusy });
        Check(backend.SaveCalls == 0);
        await using var independent = Owned();
        var otherBackend = new ControlledBackend();
        var other = new PresentationFiles(independent, otherBackend).OpenFileAsync(new()).AsTask();
        Check(otherBackend.OpenCalls == 1);
        otherBackend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Dismissed());
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Dismissed());
        Check(await first is PickerResult<IReadFileLease>.Dismissed);
        Check(await other is PickerResult<IReadFileLease>.Dismissed);
        var save = files.SaveFileAsync(new("contact.json")).AsTask();
        backend.SaveResult.SetResult(new PickerResult<ISaveFileLease>.Dismissed());
        Check(await save is PickerResult<ISaveFileLease>.Dismissed);
    }

    private static async Task LateCancellation()
    {
        await using var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        using var cancellation = new CancellationTokenSource();
        var pending = files.OpenFileAsync(new(), cancellation.Token).AsTask();
        cancellation.Cancel();
        Check(backend.Token.IsCancellationRequested);
        var lease = new ControlledLease();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Selected(lease));
        await lease.Disposing.Task;
        Check(!pending.IsCompleted);
        lease.Release.SetResult();
        await Throws<OperationCanceledException>(() => pending);
        Check(lease.Disposals == 1);
    }

    private static async Task OwnerShutdown()
    {
        var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        var snapshot = files.GetSnapshot();
        var pending = files.OpenFileAsync(new()).AsTask();
        var closed = owner.DisposeAsync().AsTask();
        Check(!closed.IsCompleted);
        Check(await files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed });
        await using var replacement = Owned();
        Check(snapshot.Generation != replacement.Generation);
        Check(snapshot.Statuses["platform.files.open"] is CapabilityStatus.Available);
        var lease = new ControlledLease();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Selected(lease));
        await lease.Disposing.Task;
        Check(!closed.IsCompleted);
        lease.Release.SetResult();
        Check(await pending is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed });
        await closed;
        await owner.DisposeAsync();
        Check(lease.Disposals == 1 && backend.OpenCalls == 1);
    }

    private static async Task CleanupFailure()
    {
        var owner = Owned();
        var backend = new ControlledBackend();
        var pending = new PresentationFiles(owner, backend).OpenFileAsync(new()).AsTask();
        var closing = owner.DisposeAsync().AsTask();
        var lease = new ControlledLease { FailRelease = true };
        lease.Release.SetResult();
        backend.OpenResult.SetResult(new PickerResult<IReadFileLease>.Selected(lease));
        await Throws<IOException>(() => pending);
        await Throws<IOException>(() => closing);
        await Throws<IOException>(() => owner.DisposeAsync().AsTask());
        Check(lease.Disposals == 1);
    }

    private static async Task ProviderFailure()
    {
        await using var owner = Owned();
        var backend = new ControlledBackend();
        var files = new PresentationFiles(owner, backend);
        backend.OpenResult.SetException(new IOException("injected provider bug"));
        await Throws<IOException>(() => files.OpenFileAsync(new()).AsTask());
        backend.SaveResult.SetResult(new PickerResult<ISaveFileLease>.Failed(FailureCode.PermissionDenied));
        Check(await files.SaveFileAsync(new("contact.json")) is PickerResult<ISaveFileLease>.Failed { Code: FailureCode.PermissionDenied });
        Check(backend.SaveCalls == 1);
        await Throws<ArgumentException>(() => files.SaveFileAsync(new("../contact.json")).AsTask());
        Check(backend.SaveCalls == 1);
    }

    private static async Task Dispatch()
    {
        await using var owner = Owned();
        var queue = new TestQueue();
        var dispatcher = new PresentationDispatcher(owner, () => queue.OnOwner, queue.Post);
        int calls = 0;
        var pending = await Task.Run(() => Task.FromResult(dispatcher.InvokeAsync(() =>
        {
            Check(queue.OnOwner); calls++;
            var inline = dispatcher.InvokeAsync(() => calls++);
            Check(inline.IsCompletedSuccessfully);
            inline.AsTask().GetAwaiter().GetResult();
        }).AsTask()));
        Check(!pending.IsCompleted && queue.Count == 1);
        queue.Pump();
        await pending;
        Check(calls == 2);
        using var cancellation = new CancellationTokenSource();
        var cancelled = dispatcher.InvokeAsync(() => calls++, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => cancelled);
        queue.Pump(); Check(calls == 2);
        var failed = dispatcher.InvokeAsync(() => throw new IOException("injected callback error")).AsTask();
        queue.Pump(); await Throws<IOException>(() => failed);
    }

    private static async Task DispatchShutdown()
    {
        var owner = Owned();
        var queue = new TestQueue();
        var dispatcher = new PresentationDispatcher(owner, () => queue.OnOwner, queue.Post);
        int calls = 0;
        var pending = dispatcher.InvokeAsync(() => calls++).AsTask();
        await owner.DisposeAsync();
        await Throws<OwnerClosedException>(() => pending);
        queue.Pump(); Check(calls == 0);
        await Throws<OwnerClosedException>(() => dispatcher.InvokeAsync(() => calls++).AsTask());

        var runningOwner = Owned();
        var runningQueue = new TestQueue();
        var runningDispatcher = new PresentationDispatcher(runningOwner, () => runningQueue.OnOwner, runningQueue.Post);
        var started = Signal();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var running = runningDispatcher.InvokeAsync(() => { started.SetResult(); release.Wait(); calls++; }, cancellation.Token).AsTask();
        var pump = Task.Run(runningQueue.Pump);
        await started.Task;
        var closing = runningOwner.DisposeAsync().AsTask();
        cancellation.Cancel();
        try { Check(!closing.IsCompleted && !running.IsCompleted); }
        finally { release.Set(); }
        await pump; await running; await closing;
        Check(calls == 1);
    }

    private static async Task Composition()
    {
        foreach (IApplicationHost host in new IApplicationHost[] {
            new DesktopApplicationHost(new() { OpenWindow = false }),
            new CsWebUiApplicationHost(new() { Assets = new UnusedAssets(), OpenWindow = false }),
        })
        {
            var builder = new RunicApplicationBuilder(new("platform.prototype", "0.0.0", "test", ["platform.files.open"]), []);
            builder.UseHost(host);
            Register(builder.Services);
            // Build the real host composition, without starting native code.
            await using var application = builder.Build();
            Check(application.Capabilities.GetRequired("platform.files.open").Availability == ApplicationCapabilityAvailability.Unavailable);

            await using var provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var firstScope = provider.CreateAsyncScope();
            await using var secondScope = provider.CreateAsyncScope();
            var first = firstScope.ServiceProvider.GetRequiredService<Feature>();
            var second = secondScope.ServiceProvider.GetRequiredService<Feature>();
            Check(first.Generation != second.Generation);
            Check(ReferenceEquals(first.Files, firstScope.ServiceProvider.GetRequiredService<IPlatformCapabilities>()));
            Check(await first.Files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured });
            await firstScope.DisposeAsync();
            Check(await first.Files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.OwnerClosed });
            Check(await second.Files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable { Reason: UnavailableReason.ProviderNotConfigured });
        }
    }

    private static void Register(IServiceCollection services)
    {
        services.AddScoped(_ => new PresentationLifetime());
        services.AddScoped(service => new PresentationFiles(service.GetRequiredService<PresentationLifetime>()));
        services.AddScoped<IFileDialogs>(service => service.GetRequiredService<PresentationFiles>());
        services.AddScoped<IPlatformCapabilities>(service => service.GetRequiredService<PresentationFiles>());
        services.AddScoped<Feature>();
    }

    private sealed class Feature(IFileDialogs files, IPlatformCapabilities capabilities)
    {
        internal IFileDialogs Files => files;
        internal Guid Generation => capabilities.GetSnapshot().Generation;
    }

    private static PresentationLifetime Owned() { var owner = new PresentationLifetime(); owner.AttachTestOwner(); return owner; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Conformance assertion failed."); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class ControlledBackend : IPickerBackend
    {
        public bool IsAvailable { get; set; } = true;
        internal int OpenCalls, SaveCalls;
        internal CancellationToken Token;
        internal TaskCompletionSource<PickerResult<IReadFileLease>> OpenResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<PickerResult<ISaveFileLease>> SaveResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default)
        { OpenCalls++; Token = cancellationToken; return new(OpenResult.Task); }
        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default)
        { SaveCalls++; Token = cancellationToken; return new(SaveResult.Task); }
    }

    private sealed class ControlledLease : IReadFileLease
    {
        internal int Disposals;
        internal bool FailRelease;
        internal TaskCompletionSource Disposing { get; } = Signal();
        internal TaskCompletionSource Release { get; } = Signal();
        public string DisplayName => "contact.json";
        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("A cancelled selection must never be read.");
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals); Disposing.TrySetResult(); await Release.Task;
            if (FailRelease) throw new IOException("injected access-release failure");
        }
    }

    private sealed class TestQueue
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _callbacks = new();
        private int _ownerThread;
        internal bool OnOwner => Environment.CurrentManagedThreadId == Volatile.Read(ref _ownerThread);
        internal int Count => _callbacks.Count;
        internal void Post(Action action) => _callbacks.Enqueue(action);
        internal void Pump()
        {
            Volatile.Write(ref _ownerThread, Environment.CurrentManagedThreadId);
            try { while (_callbacks.TryDequeue(out var callback)) callback(); }
            finally { Volatile.Write(ref _ownerThread, 0); }
        }
    }

    private sealed class UnusedAssets : IAssetSource
    {
        public AssetManifest Manifest => throw new InvalidOperationException("Composition must not load assets.");
        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Composition must not start native hosting.");
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Composition must not read files.");
    }
}
