using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Navigation;

namespace NotesWindowViews;

// Test-only check that each browser presentation receives a .NET View and
// releases only that presentation as nested content changes.
internal static class ViewLifetimeCheck
{
    public static async Task RunAsync(bool useSplat)
    {
        using var app = NotesApplication.Create(useSplat);
        await using var scope = app.Services.CreateAsyncScope();
        var vm = scope.ServiceProvider.GetRequiredService<ShellViewModel>();
        using var native = new ProbeTransport();
        var transport = native;
        // Like a real window, the session binds the shell to the window's model context. A
        // shell without one serializes its bridge with a lock instead of model turns, and a
        // navigation commit that publishes the shell could then deadlock with a snapshot.
        using var content = new WindowContentSession(transport, new WindowContentSessionOptions { ViewLocator = scope.ServiceProvider.GetRequiredService<IRunicViewLocator>(), RootModel = vm, ModelContext = scope.ServiceProvider.GetRequiredService<IRunicModelContext>() });
        var factory = scope.ServiceProvider.GetRequiredService<
            Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>();
        using (factory(transport, content, vm))
        {
            var initial = MountShell(native);
            Require<SidebarView>(1);
            Require<HomeView>(1);

            await using (var secondScope = app.Services.CreateAsyncScope())
            {
                var secondVm = secondScope.ServiceProvider.GetRequiredService<ShellViewModel>();
                if (ReferenceEquals(vm, secondVm))
                    throw new InvalidOperationException("Two window scopes share a ShellViewModel.");
                using var secondNative = new ProbeTransport();
                var secondTransport = secondNative;
                using var secondContent = new WindowContentSession(secondTransport, new WindowContentSessionOptions { ViewLocator = secondScope.ServiceProvider.GetRequiredService<IRunicViewLocator>(), RootModel = secondVm, ModelContext = secondScope.ServiceProvider.GetRequiredService<IRunicModelContext>() });
                var secondFactory = secondScope.ServiceProvider.GetRequiredService<
                    Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>();
                using (secondFactory(secondTransport, secondContent, secondVm))
                {
                    _ = MountShell(secondNative);
                    Require<SidebarView>(2);
                    Require<HomeView>(2);
                }
            }
            Require<SidebarView>(1);
            Require<HomeView>(1);

            Unmount(native, initial.Main);
            await vm.Sidebar.OpenNotesCommand.ExecuteAsync(null);
            using var documentShellState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            var document = Mount(native, ReferenceId(documentShellState, "main"));
            using var editorState = System.Text.Json.JsonDocument.Parse(native.Call($"content{document.Id}Snapshot"));
            var editor = Mount(native, ReferenceId(editorState, "currentPane"));
            Require<HomeView>(0);
            Require<DocumentView>(1);
            Require<EditorView>(1);
            Require<SidebarView>(1);

            var documentModel = (DocumentViewModel)vm.Main.Current!;
            Unmount(native, editor);
            await documentModel.ShowPreviewCommand.ExecuteAsync(null);
            using var previewState = System.Text.Json.JsonDocument.Parse(native.Call($"content{document.Id}Snapshot"));
            var preview = Mount(native, ReferenceId(previewState, "currentPane"));
            Require<EditorView>(0);
            Require<PreviewView>(1);

            // Twenty Editor/Preview remount cycles detach and reattach the nested endpoint without growing the window's
            // routes. This is deliberately bounded: it records route reuse, not a performance or endurance claim.
            var activeRoutesBeforeCycles = native.ActiveRoutes;
            for (var cycle = 0; cycle < 20; cycle++)
            {
                Unmount(native, preview);
                await documentModel.ShowEditorCommand.ExecuteAsync(null);
                using var editorCycleState = System.Text.Json.JsonDocument.Parse(
                    native.Call($"content{document.Id}Snapshot"));
                editor = Mount(native, ReferenceId(editorCycleState, "currentPane"));
                Require<EditorView>(1);
                Require<PreviewView>(0);

                Unmount(native, editor);
                await documentModel.ShowPreviewCommand.ExecuteAsync(null);
                using var previewCycleState = System.Text.Json.JsonDocument.Parse(
                    native.Call($"content{document.Id}Snapshot"));
                preview = Mount(native, ReferenceId(previewCycleState, "currentPane"));
                Require<EditorView>(0);
                Require<PreviewView>(1);
            }
            if (native.ActiveRoutes != activeRoutesBeforeCycles)
                throw new InvalidOperationException(
                    $"Twenty Editor/Preview remount cycles grew the window's routes: {activeRoutesBeforeCycles} -> {native.ActiveRoutes}.");
            Console.WriteLine($"VIEW_ROUTE_REUSE|cycles=20|active={activeRoutesBeforeCycles}->{native.ActiveRoutes}");

            Unmount(native, preview);
            await documentModel.ShowEditorCommand.ExecuteAsync(null);
            using var remountedEditorState = System.Text.Json.JsonDocument.Parse(native.Call($"content{document.Id}Snapshot"));
            editor = Mount(native, ReferenceId(remountedEditorState, "currentPane"));
            documentModel.Editor.Title = "Unsaved";
            // Leaving waits in the document's guard until the dialog answers.
            var cancelled = vm.Sidebar.OpenHomeCommand.ExecuteAsync(null);
            await UntilAsync(() => vm.Dialog.Current is not null, "The guard did not open the dialog.");
            using var cancelDialogState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            var cancelDialog = Mount(native, ReferenceId(cancelDialogState, "dialog"));
            Require<ConfirmNavigationView>(1);
            Unmount(native, cancelDialog);
            await ((ConfirmNavigationViewModel)vm.Dialog.Current!).CancelCommand.ExecuteAsync(null);
            await cancelled;
            if (vm.Main.Current != documentModel || vm.Dialog.Current is not null)
                throw new InvalidOperationException("Cancelling the dialog did not keep the document.");
            native.Call("shellSnapshot");
            Require<ConfirmNavigationView>(0);

            var confirmed = vm.Sidebar.OpenHomeCommand.ExecuteAsync(null);
            await UntilAsync(() => vm.Dialog.Current is not null, "The guard did not open the dialog again.");
            using var confirmDialogState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            _ = Mount(native, ReferenceId(confirmDialogState, "dialog"));
            Unmount(native, editor);
            Unmount(native, document);
            await ((ConfirmNavigationViewModel)vm.Dialog.Current!).ConfirmCommand.ExecuteAsync(null);
            await confirmed;
            using var homeState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            _ = Mount(native, ReferenceId(homeState, "main"));
            Require<DocumentView>(0);
            Require<EditorView>(0);
            Require<HomeView>(1);
            Require<ConfirmNavigationView>(0);
        }
        Require<SidebarView>(0);
        Require<HomeView>(0);
        Console.WriteLine($"VIEW_LIFETIME_OK|{(useSplat ? "splat" : "microsoft-di")}|two-scopes|mounted-presentations|nested|dialog|detached");
    }

    private static async Task UntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(10);
        }
    }

    private static ShellMounts MountShell(ProbeTransport transport)
    {
        using var state = System.Text.Json.JsonDocument.Parse(transport.Call("shellSnapshot"));
        return new ShellMounts(
            Mount(transport, ReferenceId(state, "sidebar")),
            Mount(transport, ReferenceId(state, "main")));
    }

    private static MountedReference Mount(ProbeTransport transport, string id)
    {
        var mounted = new MountedReference(id, $"view-lifetime:{id}");
        if (transport.Call($"content{mounted.Id}Mount", mounted.Token) != "ok")
            throw new InvalidOperationException($"The presentation {id} did not acknowledge its mount.");
        return mounted;
    }

    private static void Unmount(ProbeTransport transport, MountedReference mounted)
    {
        if (transport.Call($"content{mounted.Id}Unmount", mounted.Token) != "ok")
            throw new InvalidOperationException($"The presentation {mounted.Id} did not acknowledge its unmount.");
    }

    private static string ReferenceId(System.Text.Json.JsonDocument state, string property) =>
        state.RootElement.GetProperty("state").GetProperty(property).GetProperty("id").GetString()
        ?? throw new InvalidOperationException($"The {property} presentation has no id.");

    private sealed record ShellMounts(MountedReference Sidebar, MountedReference Main);
    private sealed record MountedReference(string Id, string Token);

    private static void Require<TView>(int expected)
    {
        var actual = ViewTrace.ActiveCount(typeof(TView));
        if (actual != expected)
            throw new InvalidOperationException($"{typeof(TView).Name}: expected {expected} active views, got {actual}.");
    }

    // The window's transport, without a page. Until the producer speaks protocol 4 (P4), the check calls the
    // generated bridges' protocol-3 Snapshot, Mount and Unmount routes through the window dispatcher's legacy routes.
    private sealed class ProbeTransport : IBridgeTransport, IDisposable
    {
        private BridgeDispatcher? _dispatcher;

        public int ActiveRoutes => _dispatcher?.Legacy.Names.Count ?? 0;

        public IDisposable Serve(IBridgeRequestHandler handler)
        {
            _dispatcher = (BridgeDispatcher)handler;
            return this;
        }

        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public string Call(string name) => Find(name)(new NoArguments());
        public string Call(string name, string value) => Find(name)(new StringArguments(value));

        public void Dispose() { }

        private Func<IBridgeArguments, string> Find(string name) =>
            _dispatcher is { } dispatcher && dispatcher.Legacy.TryFind(name, out var sync, out _) && sync is not null
                ? sync
                : throw new KeyNotFoundException($"Route '{name}' is not bound.");

        private sealed class NoArguments : IBridgeArguments
        {
            public long GetInt64() => throw new NotSupportedException();
            public bool GetBoolean() => throw new NotSupportedException();
            public string GetString() => throw new NotSupportedException();
        }

        private sealed class StringArguments(string value) : IBridgeArguments
        {
            public long GetInt64() => throw new NotSupportedException();
            public bool GetBoolean() => throw new NotSupportedException();
            public string GetString() => value;
        }
    }
}
