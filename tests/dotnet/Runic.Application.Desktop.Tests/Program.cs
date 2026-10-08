using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform.Runtime;

// Headless checks for the Desktop host adapter. A Desktop host serves its
// surface without opening a native presentation, so these run without a
// browser or WebView.
await using (var host = await DesktopHost.StartAsync())
{
    await PublishDoesNotBlockAndCoalescesPerRoute(host);
    await GeneratedCollectionDeliveryPreservesPendingFrames(host);
    await DisposedTransportReleasesRoutesAndDelivery(host);
    await DrainTimeoutClosesRoutesAndKeepsTheScopeUntilWorkEnds(host);
    await MissingRegistrationsFailBeforeASurfaceExists(host);
}
await NativeOwnerFollowsOnlyItsOpenEmbeddedWindow();
await OpenedWindowExposesItsNativeOwner();
await CancelledQueuedOwnerWorkReportsCancellation();
await PresentationWithoutNativeDispatchHasAnUnavailableOwner();
Console.WriteLine("Runic.Application.Desktop host adapter passed.");

static async Task GeneratedCollectionDeliveryPreservesPendingFrames(DesktopHost host)
{
    await using var surface = await host.CreateSurfaceAsync();
    var scripts = new List<string>();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var transport = new DesktopBridgeTransport(surface, script =>
    {
        lock (scripts)
        {
            scripts.Add(script);
            if (scripts.Count == 1) { started.TrySetResult(); return release.Task; }
        }
        return Task.CompletedTask;
    });
    var model = new OrderedCollectionModel();
    using var bridge = new OrderedCollectionBridge(transport, model);
    try
    {
        model.Rows.Add(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        model.Rows.Add(2);
        model.Rows.Add(3);
        lock (scripts) Require(scripts.Count == 1, "Dependent frames bypassed the in-flight delivery.");
        release.TrySetResult();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (scripts) if (scripts.Count == 3) break;
            await Task.Delay(5);
        }
        lock (scripts)
            Require(scripts.Count == 3 && scripts.Select((script, index) => script.Contains($"\"items\":[{index + 1}]", StringComparison.Ordinal)).All(value => value),
                "Desktop coalesced or reordered dependent collection frames.");
    }
    finally { release.TrySetResult(); }
}

static async Task PublishDoesNotBlockAndCoalescesPerRoute(DesktopHost host)
{
    await using var surface = await host.CreateSurfaceAsync();
    var scripts = new List<string>();
    var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var transport = new DesktopBridgeTransport(surface, script =>
    {
        lock (scripts) scripts.Add(script);
        return script.Contains("first", StringComparison.Ordinal) ? inFlight.Task : Task.CompletedTask;
    });

    transport.Publish("counter", "{\"value\":\"first\"}");
    transport.Publish("counter", "{\"value\":\"superseded\"}");
    transport.Publish("counter", "{\"value\":\"latest\"}");
    transport.Publish("other", "{\"value\":\"independent\"}");
    lock (scripts)
        Require(scripts.Count == 2 && scripts.Any(script => script.Contains("independent", StringComparison.Ordinal)),
            "A pending publication blocked the caller or another route.");

    inFlight.SetResult();
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        lock (scripts) if (scripts.Count == 3) break;
        await Task.Delay(10);
    }
    lock (scripts)
    {
        Require(scripts.Count == 3 && scripts[2].Contains("latest", StringComparison.Ordinal)
            && !scripts.Any(script => script.Contains("superseded", StringComparison.Ordinal)),
            "A route did not deliver only its latest pending state after the in-flight script.");
        Require(scripts[0].StartsWith("globalThis[\"__counterChanged\"]?.(", StringComparison.Ordinal),
            $"The publication script changed shape: {scripts[0]}");
    }
}

static async Task DisposedTransportReleasesRoutesAndDelivery(DesktopHost host)
{
    await using var surface = await host.CreateSurfaceAsync();
    var delivered = 0;
    var transport = new DesktopBridgeTransport(surface, _ =>
    {
        Interlocked.Increment(ref delivered);
        return Task.CompletedTask;
    });
    var first = transport.Bind("first", _ => "ok");
    _ = transport.BindAsync("second", (_, _) => ValueTask.FromResult("ok"));
    first.Dispose();
    Require(transport.RegisteredRouteCount == 1, "Releasing one route did not unregister it.");
    transport.Dispose();
    Require(transport.RegisteredRouteCount == 0, "Disposing the transport left routes registered.");
    using var late = transport.Bind("late", _ => "ok");
    transport.Publish("late", "{}");
    Require(transport.RegisteredRouteCount == 0 && Volatile.Read(ref delivered) == 0,
        "A disposed transport registered a route or delivered state.");
}

static async Task DrainTimeoutClosesRoutesAndKeepsTheScopeUntilWorkEnds(DesktopHost host)
{
    var services = new ServiceCollection();
    services.AddScoped<ScopeProbe>();
    services.AddScoped<SlowModel>();
    await using var provider = services.BuildServiceProvider();
    var scope = provider.CreateAsyncScope();
    var probe = scope.ServiceProvider.GetRequiredService<ScopeProbe>();
    var model = scope.ServiceProvider.GetRequiredService<SlowModel>();
    var surface = await host.CreateSurfaceAsync();
    var transport = new DesktopBridgeTransport(surface);
    var content = new WindowContentSession(transport, rootModel: model);
    var connections = surface.SubscribeConnectionEvents(_ => { });
    var window = new DesktopBridgeWindow<SlowModel>(scope, surface, transport, content, connections, model);
    // The command route is served by an in-memory transport so the test can
    // call it; its admission still belongs to the window's content session.
    using var calls = new InMemoryViewTransport();
    window.Attach(new SlowBridge(calls, model, content));
    Require(transport.RegisteredRouteCount > 0, "The window session registered no Desktop routes.");

    var call = calls.CallAsync("slowSave").AsTask();
    Require(await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)), "The awaited command did not start.");
    var result = await window.CloseAsync(TimeSpan.Zero);
    Require(!result.Drained && result.RemainingOperations == 1, "Close did not report the running command.");
    Require(transport.RegisteredRouteCount == 0, "A timed-out close left the window's Desktop routes registered.");
    Require(!probe.Disposed, "The window scope was disposed while accepted work was still running.");

    model.Release.SetResult();
    await result.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    Require(probe.Disposed, "The window scope was not disposed after accepted work finished.");
    using var reply = JsonDocument.Parse(await call.WaitAsync(TimeSpan.FromSeconds(5)));
    Require(reply.RootElement.TryGetProperty("ok", out _), "The awaited command did not reply after close.");
}

static async Task MissingRegistrationsFailBeforeASurfaceExists(DesktopHost host)
{
    var empty = new ServiceCollection().BuildServiceProvider();
    await using (empty)
    {
        var validation = empty.ValidateDesktopWindow<OrderedCollectionModel>(host);
        Require(validation.Warnings.Any(static item => item.Code == "viewmodel-not-registered") &&
            validation.Errors.Any(static item => item.Code == "bridge-not-registered"),
            "Validation did not report the missing Bridge as an error and the unconfirmed ViewModel as a warning.");
        var created = false;
        try
        {
            await empty.OpenDesktopWindowAsync<ProbeWindow, OrderedCollectionModel>(host, new DesktopSurfaceOptions(),
                owner => { created = true; return new ProbeWindow(owner); });
            throw new InvalidOperationException("A Window opened without its Bridge registration.");
        }
        catch (DesktopConfigurationException error)
        {
            Require(!created && error.Diagnostics.Single().Code == "bridge-not-registered" && error.Message.Contains("AddRunicViews()", StringComparison.Ordinal),
                "Opening without registrations did not fail early with an actionable message.");
        }
    }

    var services = new ServiceCollection();
    services.AddScoped<OrderedCollectionModel>();
    services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
        _ => (transport, model) => new OrderedCollectionBridge(transport, model));
    await using var provider = services.BuildServiceProvider();
    var registered = provider.ValidateDesktopWindow<OrderedCollectionModel>(host);
    Require(!registered.Diagnostics.Any(static item => item.Code is "viewmodel-not-registered" or "bridge-not-registered"),
        "Validation reported registrations that exist.");
}

static async Task NativeOwnerFollowsOnlyItsOpenEmbeddedWindow()
{
    var factory = new NativeDispatchFactory();
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
    await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
    var first = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
    var owner = new DesktopNativeOwner(first);
    Require(owner.IsAvailable && ReferenceEquals(owner.Window, first), "An open embedded window had no available native owner.");
    Require(!owner.CheckAccess(), "The owner reported native access from a worker thread.");
    nint observed = 0;
    var onNativeThread = false;
    await owner.InvokeAsync(handle => { observed = handle; onNativeThread = owner.CheckAccess(); });
    Require(observed == NativeDispatchHost.Handle && onNativeThread, "The owner did not run the callback on the window's native thread.");
    Require(new DesktopNativeOwner(first).Generation != owner.Generation, "Two owners shared one generation.");

    await first.CloseAsync();
    Require(!owner.IsAvailable, "The owner stayed available after its window closed.");
    var ran = false;
    try
    {
        await owner.InvokeAsync(_ => ran = true);
        throw new InvalidOperationException("A closed window's owner dispatched native work.");
    }
    catch (OwnerClosedException) { }
    Require(!ran, "A closed window's owner ran its callback.");

    // A replacement window does not inherit the closed window's owner.
    await using var second = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
    Require(!owner.IsAvailable && new DesktopNativeOwner(second).IsAvailable, "An owner followed the replacement window.");

    // Work queued before close must not see the closed window's handle.
    var replacementOwner = new DesktopNativeOwner(second);
    factory.Hosts[^1].HoldDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var queued = replacementOwner.InvokeAsync(_ => ran = true).AsTask();
    await second.CloseAsync();
    factory.Hosts[^1].HoldDispatch!.SetResult();
    try
    {
        await queued.WaitAsync(TimeSpan.FromSeconds(5));
        throw new InvalidOperationException("Native work queued before close ran after it.");
    }
    catch (OwnerClosedException) { }
    Require(!ran, "Native work queued before close ran after it.");
}

static async Task OpenedWindowExposesItsNativeOwner()
{
    var services = new ServiceCollection();
    services.AddScoped<OrderedCollectionModel>();
    services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
        _ => (transport, model) => new OrderedCollectionBridge(transport, model));
    await using var provider = services.BuildServiceProvider();
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
    DesktopBridgeWindow<OrderedCollectionModel>? bridge = null;
    await using var window = await provider.OpenDesktopWindowAsync<ProbeWindow, OrderedCollectionModel>(host,
        new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
        owner => { bridge = owner; try { _ = owner.NativeOwner; throw new InvalidOperationException("unexpected"); } catch (InvalidOperationException error) when (error.Message != "unexpected") { } return new ProbeWindow(owner); },
        new DesktopWindowOptions { Browser = BrowserKind.Embedded });
    Require(bridge!.NativeOwner.IsAvailable && ReferenceEquals(bridge.NativeOwner.Window, bridge.Presentation)
        && ReferenceEquals(bridge.NativeOwner, bridge.NativeOwner),
        "The opened Desktop window did not expose one available native owner for its presentation.");
}

static async Task CancelledQueuedOwnerWorkReportsCancellation()
{
    var factory = new NativeDispatchFactory();
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
    await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
    var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
    var owner = new DesktopNativeOwner(window);
    var ran = false;

    // Cancelled while queued on an open window: the caller cancelled, the owner did not close.
    factory.Hosts[^1].HoldDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var cancellation = new CancellationTokenSource())
    {
        var queued = owner.InvokeAsync(_ => ran = true, cancellation.Token).AsTask();
        cancellation.Cancel();
        factory.Hosts[^1].HoldDispatch!.SetResult();
        await RequireCancelledAsync(queued, "Cancelled queued owner work on an open window");
    }

    // Cancelled and closed while queued: cancellation still wins over the owner closure.
    factory.Hosts[^1].HoldDispatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var cancellation = new CancellationTokenSource())
    {
        var queued = owner.InvokeAsync(_ => ran = true, cancellation.Token).AsTask();
        cancellation.Cancel();
        await window.CloseAsync();
        factory.Hosts[^1].HoldDispatch!.SetResult();
        await RequireCancelledAsync(queued, "Cancelled queued owner work on a closed window");
    }
    Require(!ran, "Cancelled owner work ran its callback.");

    static async Task RequireCancelledAsync(Task queued, string subject)
    {
        try
        {
            await queued.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException($"{subject} completed.");
        }
        catch (OwnerClosedException)
        {
            throw new InvalidOperationException($"{subject} reported OwnerClosedException instead of cancellation.");
        }
        catch (OperationCanceledException) { }
    }
}

// A presentation without native dispatch, such as an installed browser after an embedded-window fallback,
// still exposes an owner, but the owner is unavailable and refuses native work.
static async Task PresentationWithoutNativeDispatchHasAnUnavailableOwner()
{
    var services = new ServiceCollection();
    services.AddScoped<OrderedCollectionModel>();
    services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
        _ => (transport, model) => new OrderedCollectionBridge(transport, model));
    await using var provider = services.BuildServiceProvider();
    await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
    {
        WindowHostFactory = new NativeDispatchFactory { SupportsDispatch = false },
        WaitForConnection = false,
    });
    DesktopBridgeWindow<OrderedCollectionModel>? bridge = null;
    await using var window = await provider.OpenDesktopWindowAsync<ProbeWindow, OrderedCollectionModel>(host,
        new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
        owner => { bridge = owner; return new ProbeWindow(owner); },
        new DesktopWindowOptions { Browser = BrowserKind.Embedded });
    var owner = bridge!.NativeOwner;
    Require(bridge.Presentation.IsOpen && ReferenceEquals(owner.Window, bridge.Presentation),
        "A presentation without native dispatch did not expose its owner.");
    Require(!owner.IsAvailable && !owner.CheckAccess(), "An owner without native dispatch reported itself available.");
    var ran = false;
    try
    {
        await owner.InvokeAsync(_ => ran = true);
        throw new InvalidOperationException("An owner without native dispatch ran native work.");
    }
    catch (OwnerClosedException) { }
    Require(!ran, "An owner without native dispatch ran its callback.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ScopeProbe : IAsyncDisposable
{
    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

sealed class OrderedCollectionModel : INotifyPropertyChanged
{
    public System.Collections.ObjectModel.ObservableCollection<int> Rows { get; } = [];
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class OrderedCollectionBridge(IBridgeTransport transport, OrderedCollectionModel model)
    : ViewModelBridge<OrderedCollectionModel>(transport, model, "ordered", static (writer, vm, revision) =>
    {
        writer.WriteStartObject(); writer.WriteNumber("revision", revision); writer.WriteStartArray("rows");
        foreach (var value in vm.Rows) writer.WriteNumberValue(value);
        writer.WriteEndArray(); writer.WriteEndObject();
    }, [new("Rows", vm => vm.Rows, null)], [], collections:
    [new("rows", vm => vm.Rows, static (writer, item) => writer.WriteNumberValue((int)item!),
        static item => ((int)item!).ToString(System.Globalization.CultureInfo.InvariantCulture))]);

sealed class SlowModel : INotifyPropertyChanged
{
    public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ICommand Save { get; } = new NoopCommand();

    // Deliberately ignores cancellation, as a save that must finish would.
    public async Task SaveAsync()
    {
        Started.TrySetResult(true);
        await Release.Task;
    }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class SlowBridge(IBridgeTransport transport, SlowModel model, WindowContentSession content)
    : ViewModelBridge<SlowModel>(transport, model, "slow", static (writer, _, revision) =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("revision", revision);
        writer.WriteEndObject();
    }, [],
    [new("Save", vm => vm.Save, ExecuteAsync: (vm, _, _) => vm.SaveAsync(), CanExecute: (_, _) => true)],
    content: content);

sealed class NoopCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
}

sealed class ProbeWindow(DesktopBridgeWindow<OrderedCollectionModel> owner)
    : RunicWindow<OrderedCollectionModel>(owner.ViewModel), IAsyncDisposable
{
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}

sealed class NativeDispatchFactory : IDesktopWindowHostFactory
{
    public List<NativeDispatchHost> Hosts { get; } = [];
    public bool SupportsDispatch { get; init; } = true;
    public bool IsSupported => true;
    public IDesktopWindowHost Create()
    {
        var host = new NativeDispatchHost { SupportsNativeDispatch = SupportsDispatch };
        Hosts.Add(host);
        return host;
    }
}

// A headless embedded host whose "native thread" is a dedicated worker.
sealed class NativeDispatchHost : IDesktopNativeDispatchWindowHost
{
    internal const nint Handle = 0x5151;
    [ThreadStatic] private static bool _onNativeThread;
    private volatile bool _open;
    public TaskCompletionSource? HoldDispatch { get; set; }
    public event EventHandler? Closed;
    public bool IsOpen => _open;
    public nint NativeHandle => _open ? Handle : 0;
    public bool SupportsNativeDispatch { get; init; } = true;
    public bool CheckNativeAccess() => _onNativeThread;
    public async ValueTask DispatchNativeAsync(Action action, CancellationToken cancellationToken)
    {
        if (HoldDispatch is { } hold) await hold.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            _onNativeThread = true;
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.Start();
        await completion.Task.ConfigureAwait(false);
    }
    public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
    { _open = true; return ValueTask.CompletedTask; }
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_open) { _open = false; Closed?.Invoke(this, EventArgs.Empty); }
        return ValueTask.CompletedTask;
    }
    public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() { _open = false; return ValueTask.CompletedTask; }
}
