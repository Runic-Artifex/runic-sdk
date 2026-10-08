using System.Reflection;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// W230-002 slice 2: a NavigationRegion<TContent> slot reaching the frontend (design record §7, §13).
internal static class NavigationPresentationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task RunAsync()
    {
        await ReferencesFollowTheRegionAsync();
        await StaleRouteWindowAsync();
        SessionTeardownIsANoOpAfterDispose();
        await ForgetWaitsForDetachmentAsync();
        await PresentationBindsOncePerWindowAsync();
        await ReconnectAndHotReloadLeaveEntriesAsync();
        await RuntimeDescriptorsObserveTheRegionAsync();
        GeneratedSlotContract();
    }

    private static async Task ReferencesFollowTheRegionAsync()
    {
        await using var window = new Window();
        var host = window.Host;
        var home = host.Root.Snapshot().Reference(vm => vm.Main)
            ?? throw new InvalidOperationException("The region's initial content was not presented.");
        Require(home.Kind == "navHome", $"The initial reference was {home}.");
        Require(window.Navigator.UnretiredEntryCount() == 1, "The initial entry is not tracked.");

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
        Require(!host.Transport.Routes.Contains($"content{secondReference.Id}Snapshot"),
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

    private static async Task StaleRouteWindowAsync()
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
        Require(Throws<KeyNotFoundException>(() => host.Transport.Call($"content{stale.Id}SetDraft", new(StringValue: "late"))),
            "An invocation on a retired entry's route was not rejected.");
        Require(page.Draft == "" && page.Disposed == 1, "The retired page was changed through its stale route.");
        var current = host.Root.Snapshot().Reference(vm => vm.Main)!;
        Require(current.Kind == "navHome", $"The next capture presented {current}.");
    }

    // Forget must not return while another flow is still detaching the content's attachment
    // (a Present replacing the slot), or the old routes stay callable after Back completes.
    private static async Task ForgetWaitsForDetachmentAsync()
    {
        using var transport = new InMemoryViewTransport();
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

    private static void SessionTeardownIsANoOpAfterDispose()
    {
        using var transport = new InMemoryViewTransport();
        var session = new WindowContentSession(transport);
        var model = new NavHomeViewModel();
        session.Dispose();
        session.Forget(model);
        session.ClearOwner(model);
    }

    private static async Task PresentationBindsOncePerWindowAsync()
    {
        var window = new Window();
        window.Navigator.BindPresentation(window.Host.Content);

        using (var otherTransport = new InMemoryViewTransport())
        using (var other = new WindowContentSession(otherTransport, modelContext: window.Context))
            Require(Throws<InvalidOperationException>(() => window.Navigator.BindPresentation(other)),
                "A second window session bound the same navigator.");
        await using (var foreignContext = new RunicModelContext())
        {
            using var foreignTransport = new InMemoryViewTransport();
            using var foreign = new WindowContentSession(foreignTransport, modelContext: foreignContext);
            Require(Throws<InvalidOperationException>(() => window.Navigator.BindPresentation(foreign)),
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
        Require(!next.Transport.Routes.Contains($"content{reference.Id}Snapshot"), "The rebound window did not forget retired content.");
        next.Dispose();
        await window.Navigator.DisposeAsync();
        await window.Context.DisposeAsync();
    }

    private static async Task ReconnectAndHotReloadLeaveEntriesAsync()
    {
        await using var window = new Window();
        var host = window.Host;
        var page = new NavEditorViewModel("mounted");
        await Wait(window.Shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(page)));
        var entry = window.Shell.Main.CurrentEntry!;
        var reference = host.Root.Snapshot().Reference(vm => vm.Main)!;
        var view = host.Root.View<NavEditorViewModel>(vm => vm.Main);
        view.Mount(connectionKey: "first");
        host.Content.ReleaseConnection("first");
        view.Mount(connectionKey: "second");
        Require(window.Shell.Main.CurrentEntry == entry && entry.State == NavigationEntryState.Active && page.Disposed == 0
            && window.Navigator.UnretiredEntryCount() == 2,
            "A disconnect or remount changed the region's entries.");

        RunicBridgeHotReload.UpdateApplication(null);
        Require(host.Root.Snapshot().Reference(vm => vm.Main) == reference && window.Shell.Main.CurrentEntry == entry,
            "RefreshAfterHotReload changed the presented entry.");
    }

    private static async Task RuntimeDescriptorsObserveTheRegionAsync()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var shell = new NavShellViewModel(navigator, new NavHomeViewModel());
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, rootModel: shell, modelContext: context);
        var observersBefore = ChangeObserverCount(shell.Main);
        using (var bridge = new RuntimeShellBridge(transport, shell, session))
        {
            transport.DrainPublications();
            await Wait(shell.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("runtime"))));
            await Until(() => transport.DrainPublications().Any(publication => publication.Route == "runtimeShell"
                && publication.StateJson.Contains("\"title\":\"runtime\"", StringComparison.Ordinal)),
                "A Bridge built from runtime descriptors did not publish when Current changed.");
            using var otherTransport = new InMemoryViewTransport();
            using var other = new WindowContentSession(otherTransport, modelContext: context);
            Require(Throws<InvalidOperationException>(() => navigator.BindPresentation(other)),
                "The runtime descriptor hook did not bind the window session.");
        }
        // Disposing the Bridge releases its PropertyChanged subscription, so the region
        // has no observer that the Bridge added.
        Require(ChangeObserverCount(shell.Main) == observersBefore,
            "Disposing the Bridge did not release its subscription to the region.");
        transport.DrainPublications();
        await Wait(shell.Main.BackAsync());
        await Task.Delay(50);
        Require(!transport.DrainPublications().Any(publication => publication.Route == "runtimeShell"),
            "A disposed Bridge still observed the region.");
    }

    private static void GeneratedSlotContract()
    {
        var parts = BridgeContractShape.Parts(typeof(NavShellViewModel));
        Require(parts.Contains("content:Runic.Application.Testing.Tests.NavShellViewModel:main:contract:default:region:Runic.Application.Testing.Tests.INavPageViewModel"),
            $"The contract fingerprint does not include the region slot:\n{string.Join('\n', parts)}");
        var generated = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "navShell.ts")).ReplaceLineEndings("\n");
        foreach (var expected in new[]
        {
            "  /** The main content; the navigator changes it. */\n  readonly main: NavEditorPageReference | NavHomePageReference | null;",
            "  readonly main: { readonly kind: \"navEditor\"; readonly id: string } | { readonly kind: \"navHome\"; readonly id: string } | null;",
            "    main: wire.main === null ? null : wire.main.kind === \"navEditor\" ? pageNavEditor(wire.main.id)",
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
        public Window()
        {
            Navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = Context });
            Shell = new NavShellViewModel(Navigator, Home);
            Host = new RunicWindowTestHost<NavShellViewModel>(Shell,
                (transport, content, vm) => new NavShellBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { ModelContext = Context, ViewLocator = new NavViewLocator() });
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

    // The number of PropertyChanged handlers on a region, read from the event's backing field.
    private static int ChangeObserverCount<T>(NavigationRegion<T> region) where T : class =>
        (typeof(NavigationRegion<T>).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(region) as Delegate)?.GetInvocationList().Length ?? 0;

    private sealed class RuntimeShellBridge(IBridgeTransport transport, NavShellViewModel vm, WindowContentSession content)
        : ViewModelBridge<NavShellViewModel>(transport, vm, "runtimeShell", Write,
            [new PropertyDescriptor<NavShellViewModel>("Main", static vm => vm.Main.Current, null, static vm => vm.Main)],
            [], content: content)
    {
        private static void Write(Utf8JsonWriter writer, NavShellViewModel vm, long revision)
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            switch (vm.Main?.Current)
            {
                case null: writer.WriteNull("title"); break;
                case var page: writer.WriteString("title", page.Title); break;
            }
            writer.WriteEndObject();
        }
    }
}
