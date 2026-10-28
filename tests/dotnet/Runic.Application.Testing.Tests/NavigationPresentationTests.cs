using System.Reflection;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W230-002 slice 2: a NavigationRegion<TContent> slot reaching the frontend (design record §7, §13).
internal sealed class NavigationPresentationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    // A pushed ViewModel without a View fails with the region and its type,
    // not an anonymous "unregistered ViewModel type".
    [Test]
    public async Task UnregisteredContentNamesRegionAndTypeAsync()
    {
        var logs = new ExceptionLog();
        await using var window = new Window(logs);
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavOrphanViewModel())));
        Exception? failure = null;
        try { window.Host.Root.Snapshot(); }
        catch (Exception error) { failure = error; }
        // Protocol 4: the snapshot answers failed without the exception, which the window logs.
        Require(failure is InvalidOperationException && failure.Message.Contains("\"kind\":\"failed\"", StringComparison.Ordinal),
            $"Presenting a ViewModel without a View did not fail the snapshot: {failure}");
        var logged = logs.Errors.SelectMany(Chain).ToArray();
        var messages = string.Join(" | ", logged.Select(error => error.Message));
        Require(logged.OfType<NotSupportedException>().Any(error =>
                error.Message.Contains("navigation region NavShellViewModel.Main", StringComparison.Ordinal)
                && error.Message.Contains(typeof(NavOrphanViewModel).FullName!, StringComparison.Ordinal)
                && error.Message.Contains("public sealed partial class NavOrphanView : RunicView<NavOrphanViewModel>;", StringComparison.Ordinal)),
            $"Presenting a ViewModel without a View did not name the region and the type: {messages}");

        static IEnumerable<Exception> Chain(Exception? error)
        {
            for (; error is not null; error = error.InnerException) yield return error;
        }
    }

    // A ViewModel without a View of its own is presented by its base class's View (#407).
    [Test]
    public async Task BaseViewPresentsDerivedContentAsync()
    {
        await using var window = new Window();
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavControllerLogViewModel())));
        var host = window.Host;
        await Until(() => host.Root.Snapshot().Reference(vm => vm.Main)?.Kind == "navLogPage",
            $"The derived ViewModel was not presented by its base class's View: {host.Root.Snapshot().Reference(vm => vm.Main)}.");
        var title = host.Root.View<NavLogPageViewModel>(vm => vm.Main).Snapshot().Read(vm => vm.Title);
        Require(title == "controller log", $"The base View presented '{title}'.");
    }

    [Test]
    public async Task WebContextCloseRejectsNavigationWithoutWaitingForGuardAsync()
    {
        await using var window = new Window();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guardToken = CancellationToken.None;
        window.Home.Guard = async token =>
        {
            guardToken = token;
            entered.TrySetResult();
            await release.Task; // A consumer hook that ignores cancellation must not block later admission.
            return true;
        };
        var pending = window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("pending"))).AsTask();
        await entered.Task.WaitAsync(Timeout);
        try
        {
            await window.Context.DisposeAsync();
            Require(window.Navigator.IsClosed, "The Views context did not signal its navigator immediately.");
            var late = window.Shell.Main.PushAsync(NavigationTarget.Borrow<INavPageViewModel>(new NavEditorViewModel("late"))).AsTask();
            Require(late.IsCompleted && await late is NavigationResult<INavPageViewModel>.Rejected { Reason: NavigationRejection.Closed },
                "A navigation on the Views path waited behind an uncancellable guard after context close.");
            await Until(() => guardToken.IsCancellationRequested, "The in-flight web guard was not cancelled on close.");
        }
        finally { release.TrySetResult(); }
        Require(await pending.WaitAsync(Timeout) is NavigationResult<INavPageViewModel>.Rejected { Reason: NavigationRejection.Closed },
            "The pending web navigation did not end Closed.");
        window.Host.Dispose();
        await window.Navigator.DisposeAsync();
        Require(!Serves(window.Host, window.Host.RootRoute) && window.Navigator.UnretiredEntryCount == 0,
            "Closing the web context leaked navigation entries or bridge routes.");
    }

    [Test]
    public async Task ReferencesFollowTheRegionAsync()
    {
        await using var window = new Window();
        var host = window.Host;
        var home = host.Root.Snapshot().Reference(vm => vm.Main)
            ?? throw new InvalidOperationException("The region's initial content was not presented.");
        Require(home.Kind == "navHome", $"The initial reference was {home}.");
        Require(window.Navigator.UnretiredEntryCount == 1, "The initial entry is not tracked.");

        var first = new NavEditorViewModel("first");
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(first)));
        await Until(() => host.Root.Snapshot().Reference(vm => vm.Main)?.Kind == "navEditor", "The pushed content was not presented.");
        var firstReference = host.Root.Snapshot().Reference(vm => vm.Main)!;
        var editor = host.Root.View<NavEditorViewModel>(vm => vm.Main);
        editor.Set(vm => vm.Draft, "draft");
        Require(first.Draft == "draft", "The presented editor did not receive the write.");
        var leases = host.Content.RetainedContentModelCount();

        var second = new NavEditorViewModel("second");
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(second)));
        var secondReference = host.Root.Snapshot().Reference(vm => vm.Main)!;
        Require(secondReference.Id != firstReference.Id, "The second editor reused the first editor's reference.");

        var back = await Wait(window.Shell.Main.BackAsync());
        Require(back is NavigationResult<INavPageViewModel>.Committed && second.Disposed == 1, $"Back gave {back}.");
        Require(!Serves(host, $"content{secondReference.Id}"),
            "The retired owned entry's routes were not forgotten.");
        Require(!RunicModelContextRegistry.Shared.TryGet(second, out _), "The retired entry kept a model-context lease.");
        var returned = host.Root.Snapshot().Reference(vm => vm.Main)!;
        Require(host.Content.RetainedContentModelCount() == leases,
            $"The window retains {host.Content.RetainedContentModelCount()} content models after Back, not {leases}.");
        Require(returned.Id == firstReference.Id && returned.Kind == "navEditor",
            $"Back presented {returned}, not the retained entry's reference {firstReference}.");
        var draft = host.Root.View<NavEditorViewModel>(vm => vm.Main).Snapshot().Read(vm => vm.Draft);
        Require(draft == "draft", $"The retained draft was '{draft}'.");

        await Wait(window.Shell.Main.ClearAsync());
        Require(host.Root.Snapshot()[nameof(NavShellViewModel.Main).ToLowerInvariant()].ValueKind == JsonValueKind.Null,
            "An empty region did not present null.");
    }

    [Test]
    public async Task StaleRouteWindowAsync()
    {
        await using var window = new Window();
        var host = window.Host;
        var page = new NavEditorViewModel("stale");
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(page)));
        var stale = host.Root.Snapshot().Reference(vm => vm.Main)!;
        Require(stale.Kind == "navEditor", "The page was not presented before Back.");

        // Retirement forgets the content when Back completes; the frontend may
        // still hold the old reference until the next capture.
        await Wait(window.Shell.Main.BackAsync());
        using (var late = JsonDocument.Parse(host.Transport.Set($"content{stale.Id}", "draft", "\"late\"")))
            Require(late.RootElement.GetProperty("error").GetProperty("kind").GetString() == "released",
                $"An invocation on a retired entry's route was not rejected: {late.RootElement}");
        Require(page.Draft == "" && page.Disposed == 1, "The retired page was changed through its stale route.");
        var current = host.Root.Snapshot().Reference(vm => vm.Main)!;
        Require(current.Kind == "navHome", $"The next capture presented {current}.");
    }

    // Forget must not return while another flow is still detaching the content's attachment
    // (a Present replacing the slot), or the old routes stay callable after Back completes.
    [Test]
    public async Task ForgetWaitsForDetachmentAsync()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport);
        var owner = new object();
        var first = new object();
        var second = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        IDisposable Attach(IBridgeTransport _, object model, string route) =>
            ReferenceEquals(model, first) ? new BlockingDisposable(entered, release) : new BlockingDisposable(null, null);

        session.Present(owner, "Main", "item", first, Attach);
        var replace = Task.Run(() => session.Present(owner, "Main", "item", second, Attach));
        await entered.Task.WaitAsync(Timeout);
        var forget = Task.Run(() => session.Forget(first));
        await Task.Delay(200);
        Require(!forget.IsCompleted, "Forget returned while the content was still detaching.");
        release.Set();
        await forget.WaitAsync(Timeout);
        await replace.WaitAsync(Timeout);
    }

    private sealed class BlockingDisposable(TaskCompletionSource? entered, ManualResetEventSlim? release) : IDisposable
    {
        public void Dispose()
        {
            entered?.TrySetResult();
            release?.Wait(TimeSpan.FromSeconds(20));
        }
    }

    [Test]
    public void SessionTeardownIsANoOpAfterDispose()
    {
        using var transport = new InMemoryBridgeTransport();
        var session = new WindowContentSession(transport);
        var model = new NavHomeViewModel();
        session.Dispose();
        session.Forget(model);
        session.ClearOwner(model);
    }

    [Test]
    public async Task PresentationBindsOncePerWindowAsync()
    {
        var window = new Window();
        NavigationPresentationBinding.Bind(window.Navigator, window.Host.Content);

        using (var otherTransport = new InMemoryBridgeTransport())
        using (var other = new WindowContentSession(otherTransport, new WindowContentSessionOptions { ModelContext = window.Context }))
            Require(Throws<InvalidOperationException>(() => NavigationPresentationBinding.Bind(window.Navigator, other)),
                "A second window session bound the same navigator.");
        await using (var foreignContext = new RunicModelContext())
        {
            using var foreignTransport = new InMemoryBridgeTransport();
            using var foreign = new WindowContentSession(foreignTransport, new WindowContentSessionOptions { ModelContext = foreignContext });
            Require(Throws<InvalidOperationException>(() => NavigationPresentationBinding.Bind(window.Navigator, foreign)),
                "A session with a different model context bound the navigator.");
            // The test host checks this through the generated Bridge.
            var mismatched = new NavShellViewModel(window.Navigator);
            Require(Throws<InvalidOperationException>(() =>
                {
                    using var host = new RunicWindowTestHost<NavShellViewModel>(mismatched,
                        (transport, content, vm) => new NavShellBridge(transport, vm, content: content),
                        new RunicWindowTestHostOptions { ModelContext = foreignContext, ViewLocator = new NavViewLocator() });
                }),
                "A test host whose ModelContext is not the navigator's presented a region.");
        }

        // The binding ends with the window session; the next window may bind.
        window.Host.Dispose();
        var next = new RunicWindowTestHost<NavShellViewModel>(window.Shell,
            (transport, content, vm) => new NavShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = window.Context, ViewLocator = new NavViewLocator() });
        var owned = new NavEditorViewModel("owned");
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(owned)));
        var reference = next.Root.Snapshot().Reference(vm => vm.Main)!;
        await Wait(window.Shell.Main.BackAsync());
        Require(!Serves(next, $"content{reference.Id}"), "The rebound window did not forget retired content.");
        next.Dispose();
        await window.Navigator.DisposeAsync();
        await window.Context.DisposeAsync();
    }

    // A closed navigator presents nothing: binding any session is silent, whether or not
    // another session bound it before it closed, and no presentation is attached.
    [Test]
    public async Task ClosedNavigatorBindsNothingAsync()
    {
        await using var context = new RunicModelContext();
        var closed = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        await closed.DisposeAsync();
        Require(closed.IsClosed, "A disposed navigator is not closed.");
        using (var firstTransport = new InMemoryBridgeTransport())
        using (var first = new WindowContentSession(firstTransport, new WindowContentSessionOptions { ModelContext = context }))
        using (var secondTransport = new InMemoryBridgeTransport())
        using (var second = new WindowContentSession(secondTransport, new WindowContentSessionOptions { ModelContext = context }))
        {
            NavigationPresentationBinding.Bind(closed, first);
            NavigationPresentationBinding.Bind(closed, second);
            Require(NavigationPresentationBinding.BoundSession(closed) is null && first.DisposalLinkCount == 0 && second.DisposalLinkCount == 0,
                "A closed navigator recorded a binding.");
        }

        var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        Require(!navigator.IsClosed, "A new navigator is closed.");
        using var boundTransport = new InMemoryBridgeTransport();
        using var bound = new WindowContentSession(boundTransport, new WindowContentSessionOptions { ModelContext = context });
        NavigationPresentationBinding.Bind(navigator, bound);
        Require(NavigationPresentationBinding.BoundSession(navigator) == bound && bound.DisposalLinkCount == 1,
            "The live navigator did not bind the session.");
        using var otherTransport = new InMemoryBridgeTransport();
        using var other = new WindowContentSession(otherTransport, new WindowContentSessionOptions { ModelContext = context });
        Require(Throws<InvalidOperationException>(() => NavigationPresentationBinding.Bind(navigator, other)),
            "A second live session bound a live navigator.");
        await navigator.DisposeAsync();
        NavigationPresentationBinding.Bind(navigator, other);
        NavigationPresentationBinding.Bind(navigator, bound);
        Require(NavigationPresentationBinding.BoundSession(navigator) is null && bound.DisposalLinkCount == 0 && other.DisposalLinkCount == 0,
            "Binding a closed navigator kept or recorded a binding.");
    }

    // Disposing a bound session detaches it, so the static binding table and the navigator
    // do not keep a closed window's session reachable while the navigator lives on.
    [Test]
    public async Task DisposedSessionIsNotRetainedAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var session = BindAndDispose(navigator, context);
        Collect(session);
        Require(!session.IsAlive, "A disposed window session stayed reachable through its navigator binding.");

        // The next session binds without a conflict.
        using var transport = new InMemoryBridgeTransport();
        using var next = new WindowContentSession(transport, new WindowContentSessionOptions { ModelContext = context });
        NavigationPresentationBinding.Bind(navigator, next);
        GC.KeepAlive(navigator);
    }

    // The reverse: a live session does not keep a navigator it presented reachable after
    // the application disposes and drops it.
    [Test]
    public async Task LiveSessionDoesNotRetainNavigatorAsync()
    {
        await using var context = new RunicModelContext();
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { ModelContext = context });
        var navigator = BindAndDisposeNavigator(session, context);
        Collect(navigator);
        Require(!navigator.IsAlive, "A live window session kept a disposed navigator reachable.");
        Require(!session.IsDisposed, "The session did not stay live.");

        // A session that presents several navigators in turn keeps no links to dead ones.
        var second = BindAndDisposeNavigator(session, context);
        Collect(second);
        Require(!second.IsAlive, "A live window session kept a second disposed navigator reachable.");
        var last = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        NavigationPresentationBinding.Bind(last, session);
        Require(session.DisposalLinkCount == 1, $"The session keeps {session.DisposalLinkCount} disposal links, not 1.");
        await last.DisposeAsync();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference BindAndDisposeNavigator(WindowContentSession session, IRunicModelContext context)
    {
        var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        NavigationPresentationBinding.Bind(navigator, session);
        Require(NavigationPresentationBinding.BoundSession(navigator) == session, "The navigator did not bind the session.");
        navigator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return new WeakReference(navigator);
    }

    private static void Collect(WeakReference reference)
    {
        for (var attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference BindAndDispose(RunicNavigator navigator, IRunicModelContext context)
    {
        var transport = new InMemoryBridgeTransport();
        var session = new WindowContentSession(transport, new WindowContentSessionOptions { ModelContext = context });
        NavigationPresentationBinding.Bind(navigator, session);
        session.Dispose();
        transport.Dispose();
        return new WeakReference(session);
    }

    [Test]
    public async Task ReconnectAndHotReloadLeaveEntriesAsync()
    {
        await using var window = new Window();
        var host = window.Host;
        var page = new NavEditorViewModel("mounted");
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(page)));
        var entry = window.Shell.Main.CurrentEntry!;
        var reference = host.Root.Snapshot().Reference(vm => vm.Main)!;
        var view = host.Root.View<NavEditorViewModel>(vm => vm.Main);
        view.Mount(connectionKey: "first");
        host.Transport.CloseConnection("first");
        view.Mount(connectionKey: "second");
        Require(window.Shell.Main.CurrentEntry == entry && entry.State == NavigationEntryState.Active && page.Disposed == 0
            && window.Navigator.UnretiredEntryCount == 2,
            "A disconnect or remount changed the region's entries.");

        RunicViewsHotReload.UpdateApplication(null);
        Require(host.Root.Snapshot().Reference(vm => vm.Main) == reference && window.Shell.Main.CurrentEntry == entry,
            "RefreshAfterHotReload changed the presented entry.");
    }

    [Test]
    public async Task RuntimeDescriptorsObserveTheRegionAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var shell = new NavShellViewModel(navigator, new NavHomeViewModel());
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { RootModel = shell, ModelContext = context });
        var observersBefore = ChangeObserverCount(shell.Main);
        using (var bridge = new RuntimeShellBridge(transport, shell, session))
        {
            // A route publishes once a client read its first state, as the page's open does.
            using (transport.SnapshotState("runtimeShell")) { }
            _ = transport.TakePushes("runtimeShell");
            await Wait(shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("runtime"))));
            await Until(() => PushedTitles(transport).Contains("runtime"),
                "A Bridge built from runtime descriptors did not publish when Current changed.");
            using var otherTransport = new InMemoryBridgeTransport();
            using var other = new WindowContentSession(otherTransport, new WindowContentSessionOptions { ModelContext = context });
            Require(Throws<InvalidOperationException>(() => NavigationPresentationBinding.Bind(navigator, other)),
                "The runtime descriptor hook did not bind the window session.");
        }
        // Disposing the Bridge releases its PropertyChanged subscription, so the region
        // has no observer that the Bridge added.
        Require(ChangeObserverCount(shell.Main) == observersBefore,
            "Disposing the Bridge did not release its subscription to the region.");
        _ = transport.TakePushes("runtimeShell");
        await Wait(shell.Main.BackAsync());
        await Task.Delay(50);
        Require(transport.TakePushes("runtimeShell").Count == 0,
            "A disposed Bridge still observed the region.");
    }

    // #648: a NavigationSelection slot publishes SelectedIndex when SelectAsync commits, and a veto publishes none.
    [Test]
    public async Task SelectionSlotPublishesTheSelectedIndexAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var inbox = new NavHomeViewModel();
        var tabs = new NavTabsViewModel(navigator, inbox, new NavEditorViewModel("archive"));
        using var host = new RunicWindowTestHost<NavTabsViewModel>(tabs,
            (transport, content, vm) => new NavTabsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        int? Shown()
        {
            var value = host.Root.Snapshot().Get(vm => vm.Tabs);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
        }
        bool Published(int index) => host.Root.TakePublications().Any(publication =>
            publication.Sets.Any(set => set.Field == "tabs" && set.Value.ValueKind == JsonValueKind.Number && set.Value.GetInt32() == index)
            || publication.State is { } state && state.GetProperty("tabs").ValueKind == JsonValueKind.Number && state.GetProperty("tabs").GetInt32() == index);
        Require(Shown() == 0 && host.Root.Snapshot().Reference(vm => vm.Inbox)?.Kind == "navHome"
                && host.Root.Snapshot().Reference(vm => vm.Archive)?.Kind == "navEditor",
            "The initial selection or the tab slots were not presented.");

        inbox.Guard = _ => ValueTask.FromResult(false);
        _ = host.Root.TakePublications();
        var vetoed = await Wait(tabs.Tabs.SelectAsync(tabs.Archive));
        Require(vetoed is NavigationResult<object>.Rejected, $"The vetoed switch gave {vetoed}.");
        await Task.Delay(50);
        Require(!Published(1) && Shown() == 0, "A vetoed switch changed the presented selection.");

        inbox.Guard = null;
        var committed = await Wait(tabs.Tabs.SelectAsync(tabs.Archive));
        Require(committed is NavigationResult<object>.Committed, $"The switch gave {committed}.");
        await Until(() => Published(1), "The Bridge did not publish the selection's new SelectedIndex.");
        Require(Shown() == 1, $"The snapshot presents tab {Shown()}, not 1.");

        // A frontend selects through a command that returns the request's outcome.
        // Protocol 4: the reply carries the revision; the state at it includes the selection.
        var reply = await host.Root.ExecuteAsync(vm => vm.SelectTabCommand, "0").WaitAsync(Timeout);
        Require(reply.Succeeded && reply.State?.Get(vm => vm.Tabs).GetInt32() == 0 && tabs.Tabs.SelectedIndex == 0,
            $"The select command did not select the first tab: {reply}");
    }

    [Test]
    public void GeneratedSlotContract()
    {
        var parts = BridgeContractShape.Parts(typeof(NavShellViewModel));
        Require(parts.Contains("content:Runic.Application.Testing.Tests.NavShellViewModel:main:contract:default:region:Runic.Application.Testing.Tests.INavPageViewModel"),
            $"The contract fingerprint does not include the region slot:\n{string.Join('\n', parts)}");
        var generated = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "navShell.ts")).ReplaceLineEndings("\n");
        foreach (var expected in new[]
        {
            "  /** The main content; the navigator changes it. */\n  readonly main: ReactiveNavEditorPageReference | NavEditorPageReference | NavHomePageReference | NavLogPagePageReference | null;",
            "  main: (wire: unknown) => { if (wire === null) return null; const reference = contentReference(wire); return reference.kind === \"reactiveNavEditor\" ? pageReactiveNavEditor(reference.id) : reference.kind === \"navEditor\" ? pageNavEditor(reference.id)",
        })
            Require(generated.Contains(expected, StringComparison.Ordinal), $"navShell.ts is missing:\n{expected}\n---\n{generated}");
        var mock = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "navShell.mock.ts"));
        Require(mock.Contains("null", StringComparison.Ordinal), "The region slot's mock is not nullable.");
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "obj", "bridge-frontend", "generated");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("The generated TypeScript directory was not found.");
    }

    // Whether the window's dispatcher still serves a route.
    private static bool Serves<TViewModel>(RunicWindowTestHost<TViewModel> host, string route) where TViewModel : class =>
        BridgeDispatcher.For(host.Transport).Serves(route);

    // The titles the runtime shell's pushes published, from its states and the set changes of its frames.
    private static List<string?> PushedTitles(InMemoryBridgeTransport transport)
    {
        List<string?> titles = [];
        foreach (var push in transport.TakePushes("runtimeShell"))
            using (push)
            {
                var root = push.RootElement;
                if (root.TryGetProperty("state", out var state)) titles.Add(state.GetProperty("title").GetString());
                if (root.TryGetProperty("changes", out var changes))
                    titles.AddRange(changes.EnumerateArray().Where(change => change.GetProperty("field").GetString() == "title")
                        .Select(change => change.GetProperty("value").GetString()));
            }
        return titles;
    }

    private static async Task<T> Wait<T>(ValueTask<T> task) => await task.AsTask().WaitAsync(Timeout);

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // One window: its scoped model context and navigator, the shell and a test host.
    private sealed class Window : IAsyncDisposable
    {
        public Window(Microsoft.Extensions.Logging.ILoggerFactory? logs = null)
        {
            Navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = Context });
            Shell = new NavShellViewModel(Navigator, Home);
            Host = new RunicWindowTestHost<NavShellViewModel>(Shell,
                (transport, content, vm) => new NavShellBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { ModelContext = Context, ViewLocator = new NavViewLocator(), LoggerFactory = logs });
        }

        public RunicModelContext Context { get; } = new();
        public RunicNavigator Navigator { get; }
        public NavHomeViewModel Home { get; } = new();
        public NavShellViewModel Shell { get; }
        public RunicWindowTestHost<NavShellViewModel> Host { get; }

        // The window close order: content first, then the scope (navigator, then context).
        public async ValueTask DisposeAsync()
        {
            Host.Dispose();
            await Navigator.DisposeAsync();
            await Context.DisposeAsync();
        }
    }

    // Records the exceptions the window logs.
    private sealed class ExceptionLog : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _errors = new();
        public IReadOnlyList<Exception> Errors => [.. _errors];
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) _errors.Enqueue(exception);
        }
    }

    // The number of PropertyChanged handlers on a region, read from the event's backing field.
    private static int ChangeObserverCount<T>(NavigationRegion<T> region) where T : class =>
        (typeof(NavigationRegion<T>).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(region) as Delegate)?.GetInvocationList().Length ?? 0;

    private sealed class RuntimeShellBridge(IBridgeTransport transport, NavShellViewModel vm, WindowContentSession content)
        : ViewModelBridge<NavShellViewModel>(transport, vm, "runtimeShell",
            [new BridgeStateMember<NavShellViewModel>("title", static (writer, vm) =>
            {
                if (vm.Main?.Current is { } page) writer.WriteStringValue(page.Title);
                else writer.WriteNullValue();
            })],
            [new PropertyDescriptor<NavShellViewModel>("Main", "main", static vm => vm.Main.Current, null, static vm => vm.Main)],
            [], [], content: content)
    {
    }
}
