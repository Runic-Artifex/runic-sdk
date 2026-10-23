using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;
using static DesktopChecks;

// Headless checks for the Desktop host adapter. A Desktop host serves its
// surface without opening a native presentation, so these run without a
// browser or WebView.

// Each case starts its own DesktopHost; the former runner shared one host between them, but every case
// creates and releases its own surface and none depends on another's routes or connections.
internal sealed class DesktopBridgeTransportTests
{
    private DesktopHost host = null!;

    [Before(HookType.Test)]
    public async Task StartHostAsync() => host = await DesktopHost.StartAsync();

    [After(HookType.Test)]
    public async Task StopHostAsync() => await host.DisposeAsync();

    [Test, NotInParallel(Order = 3)]
    public async Task GeneratedCollectionDeliveryPreservesPendingFrames()
    {
        await using var surface = await host.CreateSurfaceAsync();
        var scripts = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new DesktopBridgeTransport(surface, (_, state) =>
        {
            lock (scripts)
            {
                scripts.Add(state);
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

    [Test, NotInParallel(Order = 2)]
    public async Task PublishDoesNotBlockAndCoalescesPerRoute()
    {
        await using var surface = await host.CreateSurfaceAsync();
        var scripts = new List<string>();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new DesktopBridgeTransport(surface, (route, state) =>
        {
            lock (scripts) scripts.Add($"{route}:{state}");
            return state.Contains("first", StringComparison.Ordinal) ? inFlight.Task : Task.CompletedTask;
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
            Require(scripts[0] == "counter:{\"value\":\"first\"}",
                $"The publication changed shape: {scripts[0]}");
        }
    }

    // I-018: the UTF-8 path sends the same state bytes as the string path, without a string in between.
    [Test, NotInParallel(Order = 4)]
    public async Task Utf8PublicationSendsTheBytesOfTheStringState()
    {
        await using var surface = await host.CreateSurfaceAsync();
        var scripts = new List<(string Route, string State)>();
        var encoded = new List<(string Route, byte[] State)>();
        using var transport = new DesktopBridgeTransport(surface, (route, state) =>
        {
            lock (scripts) scripts.Add((route, state));
            return Task.CompletedTask;
        }, (route, state) =>
        {
            lock (encoded) encoded.Add((route, state.ToArray()));
            return Task.CompletedTask;
        });
        const string state = "{\"title\":\"Grüße 🎉 \\u2028\"}";
        await transport.PublishAsync("näme", state);
        await ((IBridgeUtf8Transport)transport).PublishUtf8Async("näme", System.Text.Encoding.UTF8.GetBytes(state));
        Require(scripts.Count == 1 && encoded.Count == 1 && scripts[0] == ("näme", state) && encoded[0].Route == "näme"
            && encoded[0].State.AsSpan().SequenceEqual(System.Text.Encoding.UTF8.GetBytes(scripts[0].State)),
            $"The UTF-8 state differs from the string state: {scripts.FirstOrDefault()}");
        transport.Dispose();
        await transport.PublishUtf8Async("näme", System.Text.Encoding.UTF8.GetBytes(state));
        Require(encoded.Count == 1, "A disposed transport delivered a UTF-8 frame.");
    }

    [Test, NotInParallel(Order = 4)]
    public async Task DisposedTransportReleasesRoutesAndDelivery()
    {
        await using var surface = await host.CreateSurfaceAsync();
        var delivered = 0;
        var transport = new DesktopBridgeTransport(surface, (_, _) =>
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

    [Test, NotInParallel(Order = 5)]
    public async Task DrainTimeoutClosesRoutesAndKeepsTheScopeUntilWorkEnds()
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
        var content = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = model });
        var connections = surface.SubscribeConnectionEvents(_ => { });
        var window = new DesktopBridgeWindow<SlowModel>(scope, surface, transport, content, connections, model);
        // The command route is served by an in-memory transport so the test can
        // call it; its admission still belongs to the window's content session.
        using var calls = new InMemoryBridgeTransport();
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

    [Test, NotInParallel(Order = 6)]
    public async Task MissingRegistrationsFailBeforeASurfaceExists()
    {
        var empty = new ServiceCollection().BuildServiceProvider();
        await using (empty)
        {
            var windows = new RunicDesktopHost(empty, host);
            var validation = windows.ValidateWindow<ProbeWindow>();
            Require(validation.Warnings.Any(static item => item.Code == "viewmodel-not-registered") &&
                validation.Errors.Any(static item => item.Code == "bridge-not-registered"),
                "Validation did not report the missing Bridge as an error and the unconfirmed ViewModel as a warning.");
            try
            {
                await windows.OpenWindowAsync<ProbeWindow>();
                throw new InvalidOperationException("A Window opened without its Bridge registration.");
            }
            catch (RunicWindowConfigurationException error)
            {
                Require(error.Diagnostics.Single().Code == "bridge-not-registered" && error.Message.Contains("AddRunicViews()", StringComparison.Ordinal),
                    "Opening without registrations did not fail early with an actionable message.");
            }
        }

        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        await using var provider = services.BuildServiceProvider();
        var registered = new RunicDesktopHost(provider, host).ValidateWindow<ProbeWindow>();
        Require(!registered.Diagnostics.Any(static item => item.Code is "viewmodel-not-registered" or "bridge-not-registered"),
            "Validation reported registrations that exist.");
    }
}

// Desktop windows on headless window hosts, each with its own DesktopHost.
internal sealed class DesktopWindowTests
{
    [Test, NotInParallel(Order = 7)]
    public async Task NativeOwnerFollowsOnlyItsOpenEmbeddedWindow()
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

    // vdesk-02: the options of Run's own host, which before needed a host created outside Run, reach every Window.
    [Test, NotInParallel(Order = 8)]
    public async Task RunAppliesTheHostOptionsToItsWindows()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        await using var provider = services.BuildServiceProvider();
        var factory = new NativeDispatchFactory();
        var placement = new DesktopWindowPlacement { Width = 640, Height = 480 };
        var asked = new List<IRunicWindow>();
        var hostOptions = new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded, MinimumWidth = 300, MinimumHeight = 200 },
            Placement = window => { asked.Add(window); return placement; },
        };
        var result = await Task.Run(() => RunicDesktopHost.Run(provider,
            new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false, HandleShutdownSignals = false },
            hostOptions,
            async host =>
            {
                Require(ReferenceEquals(host.WindowOptions, hostOptions.WindowOptions) && ReferenceEquals(host.SurfaceOptions, hostOptions.SurfaceOptions)
                    && ReferenceEquals(host.Placement, hostOptions.Placement) && host.Resources is null,
                    "Run did not give its host the host options.");
                await using var window = await host.OpenWindowAsync<ProbeWindow>();
                var options = factory.Hosts.Single().Options!;
                Require(asked is [var only] && ReferenceEquals(only, window) && options.Placement == placement
                    && options.Width == 640 && options.MinimumWidth == 300,
                    "The Window did not open with the host options' window options and placement.");
                return 5;
            }));
        Require(result == 5, $"Run returned {result} instead of the application's result.");
    }

    // The host options give the application's default for Bridge failure detail; a Window's own option wins.
    [Test, NotInParallel(Order = 8)]
    public async Task WindowFailureDetailOverridesTheApplicationDefault()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        var unset = new RunicDesktopHost(provider, desktop);
        var detailed = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions { IncludeFailureDetail = true });
        Require(unset.IncludeFailureDetailFor(new RunicWindowOptions()) is null
            && detailed.IncludeFailureDetailFor(new RunicWindowOptions()) == true
            && detailed.IncludeFailureDetailFor(new RunicWindowOptions { IncludeFailureDetail = false }) == false
            && unset.IncludeFailureDetailFor(new RunicWindowOptions { IncludeFailureDetail = true }) == true,
            "The Window's failure detail did not follow its own option first, then the host options.");
    }

    [Test, NotInParallel(Order = 8)]
    public async Task OpenedWindowExposesItsNativeOwner()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        await using var provider = services.BuildServiceProvider();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<ProbeWindow>();
        var bridge = (DesktopBridgeWindow<OrderedCollectionModel>)window.Host;
        Require(bridge.NativeOwner.IsAvailable && ReferenceEquals(bridge.NativeOwner.Window, bridge.Window)
            && ReferenceEquals(bridge.NativeOwner, bridge.NativeOwner),
            "The opened Desktop window did not expose one available native owner for its presentation.");
    }

    // The one Window lifecycle: OpenWindowAsync assigns the scoped ViewModel, a cancelled WaitForCloseAsync stops only
    // the wait, and a native close completes it.
    [Test, NotInParallel(Order = 9)]
    public async Task WindowLifecycleIsAsyncAndCancellable()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        await using var provider = services.BuildServiceProvider();
        var factory = new NativeDispatchFactory();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var windows = TestWindows.EmbeddedHtml(provider, host);

        await using (var window = await windows.OpenWindowAsync<ProbeWindow>(new RunicWindowOptions { Width = 640, Height = 480 }))
        {
            var bridge = (DesktopBridgeWindow<OrderedCollectionModel>)window.Host;
            Require(ReferenceEquals(window.DataContext, bridge.ViewModel) && bridge.Window.IsOpen,
                "OpenWindowAsync did not assign the scoped ViewModel and open the presentation.");
            using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                try
                {
                    await window.WaitForCloseAsync(stop.Token);
                    throw new InvalidOperationException("WaitForCloseAsync returned while the window was open.");
                }
                catch (OperationCanceledException) { }
            }
            Require(bridge.Window.IsOpen, "Cancelling WaitForCloseAsync closed the window.");

            var closed = window.WaitForCloseAsync().AsTask();
            Require(!closed.IsCompleted, "WaitForCloseAsync completed before the window closed.");
            await factory.Hosts[^1].CloseAsync();
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await using (var window = await windows.OpenWindowAsync<ProbeWindow>())
        {
            var closed = window.WaitForCloseAsync().AsTask();
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            // The token stops only the caller's wait: a close with nothing to drain completes anyway, and either way
            // the window closes.
            try { _ = await window.CloseAsync(TimeSpan.FromSeconds(5), cancelled.Token); }
            catch (OperationCanceledException) { }
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // A Runic Desktop configuration error surfaces as the exception every host throws, with the Desktop one inside,
    // and releases the Window's scope.
    [Test, NotInParallel(Order = 10)]
    public async Task DesktopConfigurationErrorsUseTheSharedException()
    {
        var services = new ServiceCollection();
        services.AddScoped<ScopeProbe>();
        services.AddScoped<OrderedCollectionModel>(provider => { _ = provider.GetRequiredService<ScopeProbe>(); return new(); });
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        await using var provider = services.BuildServiceProvider();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        var windows = new RunicDesktopHost(provider, host);
        try
        {
            await using var window = await windows.OpenWindowAsync<ProbeWindow>(
                new RunicWindowOptions { ContentRoot = AppContext.BaseDirectory, StartPage = "index.html?debug" });
            throw new InvalidOperationException("A start page with a query string opened.");
        }
        catch (RunicWindowConfigurationException error)
        {
            Require(error.InnerException is DesktopConfigurationException
                && error.Diagnostics.Single() is { Code: "content-entry-invalid", Option: "DesktopContent.Directory.Entry" },
                $"A Desktop configuration error did not become the shared exception: {error}");
        }
        Require(ScopeProbe.Last is { Disposed: true }, "A failed open kept the Window's scope.");
    }

    [Test, NotInParallel(Order = 11)]
    public async Task CancelledQueuedOwnerWorkReportsCancellation()
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
    [Test, NotInParallel(Order = 12)]
    public async Task PresentationWithoutNativeDispatchHasAnUnavailableOwner()
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
        await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<ProbeWindow>();
        var bridge = (DesktopBridgeWindow<OrderedCollectionModel>)window.Host;
        var owner = bridge.NativeOwner;
        Require(bridge.Window.IsOpen && ReferenceEquals(owner.Window, bridge.Window),
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
}

internal static class DesktopChecks
{
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class ScopeProbe : IAsyncDisposable
{
    public ScopeProbe() => Last = this;
    public static ScopeProbe? Last { get; private set; }
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
    : ViewModelBridge<OrderedCollectionModel>(transport, model, "ordered", static (writer, vm, revision, _) =>
    {
        writer.WriteStartObject(); writer.WriteNumber("revision", revision); writer.WriteStartArray("rows");
        foreach (var value in vm.Rows) writer.WriteNumberValue(value);
        writer.WriteEndArray(); writer.WriteEndObject();
    }, [new("Rows", vm => vm.Rows, null)], [], [], collections:
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
    : ViewModelBridge<SlowModel>(transport, model, "slow", static (writer, _, revision, _) =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("revision", revision);
        writer.WriteEndObject();
    }, [], [],
    [new("Save", vm => vm.Save) { ExecuteAsync = (vm, _, _) => vm.SaveAsync(), CanExecute = (_, _) => true }],
    content: content);

sealed class NoopCommand : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
}

sealed class ProbeWindow : RunicWindow<OrderedCollectionModel>;

static class TestWindows
{
    // A Window host that serves a blank page in an embedded window.
    public static RunicDesktopHost EmbeddedHtml(IServiceProvider services, DesktopHost host) => new(services, host, new RunicDesktopHostOptions
    {
        SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
        WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
    });
}

sealed class NativeDispatchFactory : IDesktopWindowHostFactory
{
    public List<NativeDispatchHost> Hosts { get; } = [];
    public bool SupportsDispatch { get; init; } = true;
    public bool Announces { get; init; }
    public bool ContextMenus { get; init; }
    // Where an open window reports its menu bar; a placement other than None also reports DesktopWindowCapabilities.MenuBar.
    public DesktopMenuBarPlacement MenuBarPlacement { get; init; }
    public bool Drags { get; init; }
    // A window host that reports its state, from this state at open, or none when it is null.
    public DesktopWindowState? InitialState { get; init; }
    public bool IsSupported => true;
    public IDesktopWindowHost Create()
    {
        var host = new NativeDispatchHost
        {
            SupportsNativeDispatch = SupportsDispatch, Announces = Announces, InitialState = InitialState, ContextMenus = ContextMenus,
            Placement = MenuBarPlacement,
            Drags = Drags,
        };
        Hosts.Add(host);
        return host;
    }
}

// A headless embedded host whose "native thread" is a dedicated worker.
sealed class NativeDispatchHost : IDesktopNativeDispatchWindowHost, IDesktopWindowStateHost, IDesktopFileDropWindowHost, IDesktopMenuWindowHost,
    IDesktopDragSourceWindowHost
{
    // Drags end with a copy.
    public bool Drags { get; init; }
    public ValueTask<DesktopDragEffects> StartDragAsync(DesktopDragData data, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DesktopDragEffects.Copy);
    // Context menus choose their first item.
    public bool ContextMenus { get; init; }
    public IDesktopMenuCommandTarget? MenuCommands { get; set; }
    public DesktopMenuBarPlacement Placement { get; init; }
    public DesktopMenuBarPlacement MenuBarPlacement => _open ? Placement : DesktopMenuBarPlacement.None;
    public event EventHandler? MenuBarPlacementChanged { add { } remove { } }
    public ValueTask SetMenuBarAsync(IReadOnlyList<DesktopMenu> menus, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<DesktopMenuItem?> ShowContextMenuAsync(System.Collections.Immutable.ImmutableArray<DesktopMenuEntry> items,
        DesktopContextMenuOptions options, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(items.OfType<DesktopMenuItem>().FirstOrDefault());
    public event EventHandler<DesktopFileDropEventArgs>? FilesDropped;
    public void Drop(DesktopFileDropEventArgs drop) => FilesDropped?.Invoke(this, drop);
    public DesktopWindowState? InitialState { get; init; }
    public DesktopWindowState? State { get; private set; }
    public DesktopWindowHostOptions? Options { get; private set; }
    public event EventHandler? StateChanged;
    public void Report(DesktopWindowState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    internal const nint Handle = 0x5151;
    [ThreadStatic] private static bool _onNativeThread;
    private volatile bool _open;
    public TaskCompletionSource? HoldDispatch { get; set; }
    public event EventHandler? Closed;
    public bool IsOpen => _open;
    public nint NativeHandle => _open ? Handle : 0;
    public bool SupportsNativeDispatch { get; init; } = true;
    public bool Announces { get; init; }
    public DesktopWindowCapabilities Capabilities => DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.Focus |
        DesktopWindowCapabilities.Minimize | DesktopWindowCapabilities.Maximize | DesktopWindowCapabilities.Resize |
        DesktopWindowCapabilities.Move | (Announces ? DesktopWindowCapabilities.Announcements : DesktopWindowCapabilities.None) |
        (ContextMenus ? DesktopWindowCapabilities.ContextMenu : DesktopWindowCapabilities.None) |
        (Placement != DesktopMenuBarPlacement.None ? DesktopWindowCapabilities.MenuBar : DesktopWindowCapabilities.None) |
        (Drags ? DesktopWindowCapabilities.FileDrag : DesktopWindowCapabilities.None) |
        (InitialState is null ? DesktopWindowCapabilities.None : DesktopWindowCapabilities.State);
    public System.Collections.Concurrent.ConcurrentQueue<(string Message, DesktopAnnouncementPriority Priority)> Announcements { get; } = new();
    public ValueTask AnnounceAsync(string message, DesktopAnnouncementPriority priority, CancellationToken cancellationToken = default)
    {
        Announcements.Enqueue((message, priority));
        return ValueTask.CompletedTask;
    }
    public bool CheckNativeAccess() => _onNativeThread;
    public async ValueTask InvokeNativeAsync(Action action, CancellationToken cancellationToken)
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
    { Options = options; State = InitialState; _open = true; return ValueTask.CompletedTask; }
    // The URLs the window's WebView was navigated to after it opened.
    public System.Threading.Channels.Channel<Uri> Navigations { get; } = System.Threading.Channels.Channel.CreateUnbounded<Uri>();
    public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default)
    {
        Navigations.Writer.TryWrite(url);
        return ValueTask.CompletedTask;
    }
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_open) { _open = false; Closed?.Invoke(this, EventArgs.Empty); }
        return ValueTask.CompletedTask;
    }
    public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() { _open = false; return ValueTask.CompletedTask; }
}
