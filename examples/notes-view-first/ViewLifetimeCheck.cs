using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace NotesWindowViews;

// Test-only check that each browser presentation receives a .NET View and
// releases only that presentation as nested content changes.
internal static class ViewLifetimeCheck
{
    public static void Run(bool useSplat)
    {
        using var app = NotesApplication.Create(useSplat);
        using var scope = app.Services.CreateScope();
        var vm = scope.ServiceProvider.GetRequiredService<ShellViewModel>();
        var native = new ProbeTransport();
        using var transport = new RebindableBridgeTransport(native);
        using var content = new WindowContentSession(transport,
            scope.ServiceProvider.GetRequiredService<IRunicViewLocator>());
        var factory = scope.ServiceProvider.GetRequiredService<
            Func<IBridgeTransport, WindowContentSession, ShellViewModel, IDisposable>>();
        using (factory(transport, content, vm))
        {
            var initial = MountShell(native);
            Require<SidebarView>(1);
            Require<HomeView>(1);

            using (var secondScope = app.Services.CreateScope())
            {
                var secondVm = secondScope.ServiceProvider.GetRequiredService<ShellViewModel>();
                if (ReferenceEquals(vm, secondVm))
                    throw new InvalidOperationException("Two window scopes share a ShellViewModel.");
                var secondNative = new ProbeTransport();
                using var secondTransport = new RebindableBridgeTransport(secondNative);
                using var secondContent = new WindowContentSession(secondTransport,
                    secondScope.ServiceProvider.GetRequiredService<IRunicViewLocator>());
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
            vm.Sidebar.OpenNotesCommand.Execute(null);
            using var documentShellState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            var document = Mount(native, ReferenceId(documentShellState, "main"));
            using var editorState = System.Text.Json.JsonDocument.Parse(native.Call($"content{document.Id}Snapshot"));
            var editor = Mount(native, ReferenceId(editorState, "currentPane"));
            Require<HomeView>(0);
            Require<DocumentView>(1);
            Require<EditorView>(1);
            Require<SidebarView>(1);

            var documentModel = (DocumentViewModel)vm.Main;
            Unmount(native, editor);
            documentModel.ShowPreviewCommand.Execute(null);
            using var previewState = System.Text.Json.JsonDocument.Parse(native.Call($"content{document.Id}Snapshot"));
            var preview = Mount(native, ReferenceId(previewState, "currentPane"));
            Require<EditorView>(0);
            Require<PreviewView>(1);

            // CS-WebUI has no per-route unbind today.  The bridge adapter must
            // consequently keep its one native binding per stable page route
            // while the nested Editor/Preview endpoint detaches and reattaches.
            // This is deliberately bounded: it records route reuse, not a
            // performance or endurance claim.
            var nativeBindingsBeforeCycles = native.BindCount;
            var activeRoutesBeforeCycles = native.ActiveRoutes;
            for (var cycle = 0; cycle < 20; cycle++)
            {
                Unmount(native, preview);
                documentModel.ShowEditorCommand.Execute(null);
                using var editorCycleState = System.Text.Json.JsonDocument.Parse(
                    native.Call($"content{document.Id}Snapshot"));
                editor = Mount(native, ReferenceId(editorCycleState, "currentPane"));
                Require<EditorView>(1);
                Require<PreviewView>(0);

                Unmount(native, editor);
                documentModel.ShowPreviewCommand.Execute(null);
                using var previewCycleState = System.Text.Json.JsonDocument.Parse(
                    native.Call($"content{document.Id}Snapshot"));
                preview = Mount(native, ReferenceId(previewCycleState, "currentPane"));
                Require<EditorView>(0);
                Require<PreviewView>(1);
            }
            if (native.BindCount != nativeBindingsBeforeCycles || native.ActiveRoutes != activeRoutesBeforeCycles)
                throw new InvalidOperationException(
                    $"Twenty Editor/Preview remount cycles grew native routes: " +
                    $"bindings {nativeBindingsBeforeCycles} -> {native.BindCount}, " +
                    $"active {activeRoutesBeforeCycles} -> {native.ActiveRoutes}.");
            Console.WriteLine(
                $"VIEW_NATIVE_ROUTE_REUSE|cycles=20|bindings={nativeBindingsBeforeCycles}->{native.BindCount}|" +
                $"active={activeRoutesBeforeCycles}->{native.ActiveRoutes}");

            Unmount(native, preview);
            documentModel.ShowEditorCommand.Execute(null);
            using var remountedEditorState = System.Text.Json.JsonDocument.Parse(native.Call($"content{document.Id}Snapshot"));
            editor = Mount(native, ReferenceId(remountedEditorState, "currentPane"));
            documentModel.Editor.Title = "Unsaved";
            vm.Sidebar.OpenHomeCommand.Execute(null);
            using var cancelDialogState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            var cancelDialog = Mount(native, ReferenceId(cancelDialogState, "dialog"));
            Require<ConfirmNavigationView>(1);
            Unmount(native, cancelDialog);
            ((ConfirmNavigationViewModel)vm.Dialog!).CancelCommand.Execute(null);
            native.Call("shellSnapshot");
            Require<ConfirmNavigationView>(0);

            vm.Sidebar.OpenHomeCommand.Execute(null);
            using var confirmDialogState = System.Text.Json.JsonDocument.Parse(native.Call("shellSnapshot"));
            _ = Mount(native, ReferenceId(confirmDialogState, "dialog"));
            Unmount(native, editor);
            Unmount(native, document);
            ((ConfirmNavigationViewModel)vm.Dialog!).ConfirmCommand.Execute(null);
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

    private sealed class ProbeTransport : IBridgeTransport
    {
        private readonly Dictionary<string, Func<IBridgeArguments, string>> _handlers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<IBridgeArguments, CancellationToken, ValueTask<string>>> _asyncHandlers = new(StringComparer.Ordinal);
        public int BindCount { get; private set; }
        public int ActiveRoutes => _handlers.Count + _asyncHandlers.Count;

        public IDisposable Bind(string name, Func<IBridgeArguments, string> handler)
        {
            _handlers.Add(name, handler);
            BindCount++;
            return new Binding(() => _handlers.Remove(name));
        }

        public IDisposable BindAsync(string name,
            Func<IBridgeArguments, CancellationToken, ValueTask<string>> handler)
        {
            _asyncHandlers.Add(name, handler);
            BindCount++;
            return new Binding(() => _asyncHandlers.Remove(name));
        }

        public void Publish(string name, string stateJson) { }

        public string Call(string name) => _handlers[name](new NoArguments());
        public string Call(string name, string value) => _handlers[name](new StringArguments(value));

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

        private sealed class Binding(Action release) : IDisposable
        {
            public void Dispose() => release();
        }
    }
}
