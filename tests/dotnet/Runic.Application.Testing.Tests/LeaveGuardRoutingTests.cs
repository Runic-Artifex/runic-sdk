using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W250-043: a ReactiveLeaveConfirmation.WithInteraction guard asks the departing page's mounted web View when the
// page's own window starts the departure: the shell's Back command from the same browser connection, or the window's
// guarded close. An interaction never reaches another window or connection, .NET handlers keep ReactiveUI's
// latest-first order, and a prompt whose View or window goes away fails closed.
internal sealed class LeaveGuardRoutingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string Client = "editor-client";
    private const string Connection = "editor-connection";
    private static readonly string Contract =
        $"{BridgeContractShape.Compute(typeof(ReactiveNavEditorViewModel))}:interaction:ConfirmDiscard";

    // The shell's Back, started in the browser, asks the editor's mounted View: No keeps the editor and its draft,
    // Yes leaves and discards it. The ViewModel's earlier fallback handler is not asked while the View answers.
    [Test]
    public async Task ShellBackAsksTheDepartingWebViewAsync()
    {
        await using var shell = await GuardedShell.OpenAsync();
        var fallbackAsked = 0;
        var editor = await shell.OpenEditorAsync(model => model.ConfirmDiscard.RegisterHandler(context =>
        {
            Interlocked.Increment(ref fallbackAsked);
            context.SetOutput(false);
        }));

        var prompt = editor.WaitForPromptAsync();
        var back = shell.BackAsync(Client, Connection);
        editor.Answer(await ExpectPromptAsync(prompt), discard: false);
        var kept = await back.WaitAsync(Timeout);
        Require(kept == "rejected:guard" && shell.ViewModel.Main.Current == editor.ViewModel
            && editor.ViewModel.Draft == "changed", $"Keep editing gave {kept}; the editor and its draft must stay.");

        prompt = editor.WaitForPromptAsync();
        back = shell.BackAsync(Client, Connection);
        editor.Answer(await ExpectPromptAsync(prompt), discard: true);
        var left = await back.WaitAsync(Timeout);
        Require(left == "committed" && shell.ViewModel.Main.Current is NavHomeViewModel && editor.ViewModel.Draft == "",
            $"Discard gave {left}; the editor must leave and discard its draft.");
        Require(Volatile.Read(ref fallbackAsked) == 0, "The ViewModel's fallback answered although the web View did.");
    }

    // ReactiveUI asks the latest handler first. A .NET handler registered after the View attached answers before the
    // browser; the browser answers before a fallback registered in the ViewModel's constructor; the fallback answers
    // when no View is mounted.
    [Test]
    public async Task NetHandlersKeepReactiveUiPrecedenceAsync()
    {
        await using var shell = await GuardedShell.OpenAsync();
        var fallbackAsked = 0;
        var editor = await shell.OpenEditorAsync(model => model.ConfirmDiscard.RegisterHandler(context =>
        {
            Interlocked.Increment(ref fallbackAsked);
            context.SetOutput(false);
        }));

        var prompt = editor.WaitForPromptAsync();
        string outcome;
        var laterAsked = 0;
        using (editor.ViewModel.ConfirmDiscard.RegisterHandler(context =>
        {
            Interlocked.Increment(ref laterAsked);
            context.SetOutput(false);
        }))
            outcome = await shell.BackAsync(Client, Connection).WaitAsync(Timeout);
        Require(outcome == "rejected:guard" && laterAsked == 1 && !prompt.IsCompleted,
            $"A .NET handler registered after the View did not answer first ({outcome}, asked {laterAsked}).");

        var back = shell.BackAsync(Client, Connection);
        editor.Answer(await ExpectPromptAsync(prompt), discard: false);
        outcome = await back.WaitAsync(Timeout);
        Require(outcome == "rejected:guard" && Volatile.Read(ref fallbackAsked) == 0,
            $"The web View did not answer before the constructor's fallback ({outcome}).");

        editor.View.Unmount(editor.Mount, Client, Connection);
        outcome = await shell.BackAsync(Client, Connection).WaitAsync(Timeout);
        Require(outcome == "rejected:guard" && Volatile.Read(ref fallbackAsked) == 1 && editor.ViewModel.Draft == "changed",
            $"Without a mounted View the fallback did not answer ({outcome}).");
    }

    // A guarded close asks the editor's mounted View, whether the user's close button or RequestCloseAsync started it:
    // No keeps the window open with the editor and its draft, Yes discards the draft and closes the window.
    [Test]
    public async Task GuardedCloseAsksTheDepartingWebViewAsync()
    {
        await using var shell = await GuardedShell.OpenAsync();
        var editor = await shell.OpenEditorAsync();

        var prompt = editor.WaitForPromptAsync();
        var userClose = shell.Native.UserCloseAsync();
        editor.Answer(await ExpectPromptAsync(prompt), discard: false);
        Require(!await userClose.WaitAsync(Timeout) && shell.Native.IsOpen
            && shell.ViewModel.Main.Current == editor.ViewModel && editor.ViewModel.Draft == "changed",
            "Keep editing on the close button closed the window or lost the draft.");

        prompt = editor.WaitForPromptAsync();
        var request = shell.Window.RequestCloseAsync().AsTask();
        editor.Answer(await ExpectPromptAsync(prompt), discard: true);
        Require(await request.WaitAsync(Timeout) && !shell.Native.IsOpen && editor.ViewModel.Draft == "",
            "Discard on RequestCloseAsync did not discard the draft and close the window.");
    }

    // The ReactiveUI RequestCloseAsync sample of the Runic.Application.Views README and the Runic.Navigation.ReactiveUI
    // walkthrough: a ReactiveCommand asks the guarded window to close, and reports when the page's web View keeps it open.
    [Test]
    public async Task AReactiveCommandRequestsTheGuardedCloseAsync()
    {
        await using var shell = await GuardedShell.OpenAsync();
        var editor = await shell.OpenEditorAsync();
        var workspace = new WorkspaceViewModel(shell.Window);

        var prompt = editor.WaitForPromptAsync();
        var kept = ReactiveCommandExecution.Execute(workspace.CloseEditorCommand, RxVoid.Default, CancellationToken.None);
        editor.Answer(await ExpectPromptAsync(prompt), discard: false);
        await kept.WaitAsync(Timeout);
        Require(workspace.StatusText == "The editor has unsaved changes." && shell.Native.IsOpen
            && editor.ViewModel.Draft == "changed", "Keep editing did not keep the window open and report it.");

        prompt = editor.WaitForPromptAsync();
        var closed = ReactiveCommandExecution.Execute(workspace.CloseEditorCommand, RxVoid.Default, CancellationToken.None);
        editor.Answer(await ExpectPromptAsync(prompt), discard: true);
        await closed.WaitAsync(Timeout);
        Require(!shell.Native.IsOpen && editor.ViewModel.Draft == "", "Discard did not close the window.");
    }

    // The shell's Back from another browser connection does not reach the editor's View, and fails closed without a
    // .NET handler. A close asks no View when two connections have mounted a handler, and fails closed too.
    [Test]
    public async Task InteractionsStayWithTheirConnectionAsync()
    {
        await using var shell = await GuardedShell.OpenAsync();
        var editor = await shell.OpenEditorAsync();
        var prompt = editor.WaitForPromptAsync();

        var outcome = await shell.BackAsync("other-client", "other-connection").WaitAsync(Timeout);
        Require(outcome == "failed" && !prompt.IsCompleted && shell.ViewModel.Main.Current == editor.ViewModel,
            $"Back from another connection gave {outcome} or reached this connection's View.");

        var second = editor.MountAgain("second-client", "second-connection");
        var secondPrompt = second.WaitForPromptAsync();
        Require(!await shell.Native.UserCloseAsync().WaitAsync(Timeout) && shell.Native.IsOpen,
            "A close with two mounted handlers closed the window.");
        Require(!prompt.IsCompleted && !secondPrompt.IsCompleted, "A close with two mounted handlers asked one of them.");
    }

    // A ViewModel presented in two windows: a scope of one window never reaches the other's presentation, even with the
    // same client and connection keys. The window's own close scope reaches its own presentation.
    [Test]
    public async Task InteractionsStayInTheirWindowAsync()
    {
        using var transportA = new InMemoryBridgeTransport();
        using var transportB = new InMemoryBridgeTransport();
        using var windowA = new WindowContentSession(transportA);
        using var windowB = new WindowContentSession(transportB);
        var model = new SharedPromptModel();
        const string route = "shared";
        var fallbackAsked = 0;
        using var fallback = model.Confirm.RegisterHandler(context =>
        {
            Interlocked.Increment(ref fallbackAsked);
            context.SetOutput(false);
        });
        var descriptor = ReactiveInteractionDescriptor.Create<SharedPromptModel, string, bool>("confirm", "testing.confirm.v1",
            value => value.Confirm, static value => JsonSerializer.Serialize(value), static value => value.GetBoolean());
        using var attachedA = descriptor.Attach(windowA, model, route);
        using var attachedB = descriptor.Attach(windowB, model, route);
        using var mountsA = windowA.AttachRootInteractionPresentation(route);
        using var mountsB = windowB.AttachRootInteractionPresentation(route);
        using var serverA = new RouteServerBridge(transportA, route, windowA);
        using var serverB = new RouteServerBridge(transportB, route, windowB);
        using (var opened = JsonDocument.Parse(transportA.Open(route, "a-present", Connection, Client,
            """[{"name":"confirm","contract":"testing.confirm.v1"}]""")))
            Require(opened.RootElement.GetProperty("presentation").GetString() == "ok"
                && opened.RootElement.GetProperty("registration").GetString() == "ok", $"Window A's presentation did not open: {opened.RootElement}");
        var waitA = NextPromptAsync(transportA, Connection, "a-present", []);

        using (RunicInteractionInvocation.Enter(windowB, route, Client, Connection))
            Require(!await ((Task<bool>)model.Confirm.Handle("from window B's command")).WaitAsync(Timeout),
                "Window B's command did not fall back to the .NET handler.");
        using (RunicInteractionInvocation.EnterWindowClose(windowB, CancellationToken.None))
            Require(!await ((Task<bool>)model.Confirm.Handle("from window B's close")).WaitAsync(Timeout),
                "Window B's close did not fall back to the .NET handler.");
        Require(fallbackAsked == 2 && !waitA.IsCompleted, "A scope of window B reached window A's presentation.");

        Task<bool> answer;
        using (RunicInteractionInvocation.EnterWindowClose(windowA, CancellationToken.None))
            answer = model.Confirm.Handle("from window A's close");
        using var request = JsonDocument.Parse(await waitA.WaitAsync(Timeout));
        var root = request.RootElement;
        Require(root.GetProperty("input").GetString() == "from window A's close", $"Window A's close was not asked: {root}.");
        Require(Result(transportA.Send(ReplyJson(route, "a-present", root, "confirm", "testing.confirm.v1", true), Connection, Client)) == "ok"
            && await answer.WaitAsync(Timeout),
            "Window A's presentation did not answer its own close.");
    }

    // A View that unmounts while it asks fails the departure closed, for Back and for a close; closing the window
    // without asking ends a pending close decision at once.
    [Test]
    public async Task APromptWhoseViewOrWindowGoesAwayFailsClosedAsync()
    {
        await using (var shell = await GuardedShell.OpenAsync())
        {
            var editor = await shell.OpenEditorAsync();
            var prompt = editor.WaitForPromptAsync();
            var back = shell.BackAsync(Client, Connection);
            await ExpectPromptAsync(prompt);
            editor.View.Unmount(editor.Mount, Client, Connection);
            var outcome = await back.WaitAsync(Timeout);
            Require(outcome == "failed" && shell.ViewModel.Main.Current == editor.ViewModel && editor.ViewModel.Draft == "changed",
                $"Back whose View unmounted mid-prompt gave {outcome}; the editor and its draft must stay.");

            var again = editor.MountAgain(Client, Connection);
            prompt = again.WaitForPromptAsync();
            var close = shell.Native.UserCloseAsync();
            await ExpectPromptAsync(prompt);
            again.View.Unmount(again.Mount, Client, Connection);
            Require(!await close.WaitAsync(Timeout) && shell.Native.IsOpen && shell.ViewModel.Main.Current == editor.ViewModel,
                "A close whose View unmounted mid-prompt closed the window.");
        }

        await using (var shell = await GuardedShell.OpenAsync())
        {
            var editor = await shell.OpenEditorAsync();
            var prompt = editor.WaitForPromptAsync();
            var request = shell.Window.RequestCloseAsync().AsTask();
            await ExpectPromptAsync(prompt);
            await shell.Window.CloseAsync(TimeSpan.FromSeconds(5)).AsTask().WaitAsync(Timeout);
            Require(await request.WaitAsync(Timeout) && !shell.Native.IsOpen,
                "Closing the window without asking did not end the pending close decision.");
        }
    }

    private static async Task<string> ExpectPromptAsync(Task<string> wait)
    {
        var json = await wait.WaitAsync(Timeout);
        using var request = JsonDocument.Parse(json);
        var root = request.RootElement;
        Require(root.GetProperty("kind").GetString() == "interaction"
            && root.GetProperty("input").GetProperty("title").GetString() == "reactive editor"
            && root.GetProperty("input").GetProperty("draft").GetString() == "changed",
            $"The web View did not receive the typed leave prompt: {root}.");
        return json;
    }

    private static string? Result(string json)
    {
        using var reply = JsonDocument.Parse(json);
        return reply.RootElement.TryGetProperty("result", out var result) ? result.GetString() : json;
    }

    // {"kind":"interaction-reply"} answering an interaction push.
    private static string ReplyJson(string route, string presentation, JsonElement request, string name, string contract, bool output) =>
        BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "interaction-reply");
            writer.WriteString("route", route);
            writer.WriteString("presentation", presentation);
            writer.WriteString("requestId", request.GetProperty("requestId").GetString());
            writer.WriteNumber("ownerEpoch", request.GetProperty("ownerEpoch").GetInt64());
            writer.WriteString("name", name);
            writer.WriteString("contract", contract);
            writer.WriteString("outcome", "answered");
            writer.WriteBoolean("output", output);
        });

    // The next interaction push to the connection's presentation that is not in seen, as JSON; it is added to seen.
    private static async Task<string> NextPromptAsync(InMemoryBridgeTransport transport, string connection, string presentation,
        HashSet<string> seen)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            foreach (var message in transport.Messages)
            {
                if (message.ConnectionKey != connection) continue;
                using var document = JsonDocument.Parse(message.Json);
                var root = document.RootElement;
                if (root.GetProperty("kind").GetString() != "interaction" || root.GetProperty("presentation").GetString() != presentation)
                    continue;
                lock (seen)
                    if (seen.Add(root.GetProperty("requestId").GetString()!)) return message.Json;
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No interaction reached {presentation}.");
            await Task.Delay(10);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The shell did not settle.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Keeps a Window that OpenWindowAsync returned and closes it from a command, as the README sample does.
    private sealed class WorkspaceViewModel : ReactiveObject
    {
        private readonly IRunicWindow _editor;
        private string _statusText = "";

        public WorkspaceViewModel(IRunicWindow editor)
        {
            _editor = editor;
            CloseEditorCommand = ReactiveCommand.CreateFromTask(async token =>
            {
                if (!await _editor.RequestCloseAsync(token)) StatusText = "The editor has unsaved changes.";
            });
        }

        public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

        public ReactiveCommand<RxVoid, RxVoid> CloseEditorCommand { get; }
    }

    private sealed class SharedPromptModel
    {
        public Interaction<string, bool> Confirm { get; } = new();
    }

    // The guarded ReactiveNavShellWindow, opened through OpenWindowAsync on a TestWindowHost.
    private sealed class GuardedShell : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private int _requests;

        private GuardedShell(ServiceProvider provider, ReactiveNavShellWindow window)
        {
            _provider = provider;
            Window = window;
            Native = (TestWindow<ReactiveNavShellViewModel>)window.Host;
        }

        public ReactiveNavShellWindow Window { get; }
        public TestWindow<ReactiveNavShellViewModel> Native { get; }
        public ReactiveNavShellViewModel ViewModel => Window.ViewModel;
        private InMemoryBridgeTransport Transport => Native.Host.Transport;

        public static async Task<GuardedShell> OpenAsync()
        {
            Require(ReactiveSchedulers.InstallMainThreadScheduler(),
                "The model-context main-thread scheduler was not installed.");
            var services = new ServiceCollection();
            services.AddRunicNavigation();
            services.AddScoped<NavHomeViewModel>();
            services.AddScoped<ReactiveNavShellViewModel>();
            services.AddScoped<Func<IBridgeTransport, WindowContentSession, ReactiveNavShellViewModel, IDisposable>>(
                _ => (transport, content, model) => new ReactiveNavShellBridge(transport, model, content: content));
            var provider = services.BuildServiceProvider();
            try { return new GuardedShell(provider, await new TestWindowHost(provider).OpenWindowAsync<ReactiveNavShellWindow>()); }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }

        // Pushes an editor with a draft; the browser mounts its View with a confirmDiscard handler.
        public async Task<EditorPresentation> OpenEditorAsync(Action<ReactiveNavEditorViewModel>? created = null)
        {
            var request = ViewModel.Main.PushForResult<string>(NavigationTarget.Create<INavPageViewModel>((_, entry) =>
            {
                var editor = new ReactiveNavEditorViewModel(entry);
                created?.Invoke(editor);
                return editor;
            }));
            Require(await request.Transition.AsTask().WaitAsync(Timeout) is NavigationResult<INavPageViewModel>.Committed,
                "The editor was not pushed.");
            await Until(() => Native.Host.Root.Snapshot().Reference(vm => vm.Main)?.Kind == "reactiveNavEditor"
                && ReactiveCommandExecution.CanExecute(ViewModel.BackCommand, RxVoid.Default));
            var view = Native.Host.Root.View<ReactiveNavEditorViewModel>(vm => vm.Main);
            var presentation = EditorPresentation.Open(Transport, (ReactiveNavEditorViewModel)ViewModel.Main.Current!, view,
                Client, Connection);
            view.Set(vm => vm.Draft, "changed");
            return presentation;
        }

        // The shell's Back, started by the browser client on the connection as an operation; returns the settled outcome.
        public async Task<string> BackAsync(string client, string connection)
        {
            var requestId = $"back-{Interlocked.Increment(ref _requests)}";
            using (var admission = JsonDocument.Parse(Transport.Send(BackRequest("start", requestId), connection, client)))
                Require(admission.RootElement.GetProperty("ok").GetBoolean()
                    && admission.RootElement.GetProperty("admission").GetString() == "accepted",
                    $"The web Back command was not admitted: {admission.RootElement}.");
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                using var reply = JsonDocument.Parse(Transport.Send(BackRequest("status", requestId), connection, client));
                var status = reply.RootElement.GetProperty("status");
                var kind = status.GetProperty("kind").GetString()!;
                if (kind != "running")
                {
                    if (kind != "succeeded") return kind;
                    var result = status.GetProperty("result");
                    return result.GetProperty("kind").GetString()!.ToLowerInvariant()
                        + (result.GetProperty("rejection").ValueKind == JsonValueKind.Null ? ""
                            : ":" + result.GetProperty("rejection").GetString()!.ToLowerInvariant());
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException("The web Back did not settle.");
                await Task.Delay(10);
            }
        }

        private string BackRequest(string kind, string requestId) => BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", kind);
            writer.WriteString("route", Native.Host.RootRoute);
            writer.WriteString("member", "back");
            writer.WriteString("operation", requestId);
            writer.WriteString("contract", $"{typeof(ReactiveNavShellViewModel).FullName}:{BridgeContractShape.Compute(typeof(ReactiveNavShellViewModel))}");
        });

        public async ValueTask DisposeAsync()
        {
            await Window.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    // The browser side of one open editor View: it registers its confirmDiscard handler, waits for the guard's question
    // and answers it.
    private sealed record EditorPresentation(InMemoryBridgeTransport Transport, ReactiveNavEditorViewModel ViewModel,
        RunicViewDriver<ReactiveNavEditorViewModel> View, string Mount, string Client, string Connection)
    {
        private readonly HashSet<string> _seen = [];

        // Opens the View on the connection and registers the handler as generation 1 (open registers none).
        public static EditorPresentation Open(InMemoryBridgeTransport transport, ReactiveNavEditorViewModel viewModel,
            RunicViewDriver<ReactiveNavEditorViewModel> view, string client, string connection)
        {
            var presentation = new EditorPresentation(transport, viewModel, view, view.Mount(clientKey: client, connectionKey: connection),
                client, connection);
            presentation.Register();
            return presentation;
        }

        public EditorPresentation MountAgain(string client, string connection) => Open(Transport, ViewModel, View, client, connection);

        private void Register()
        {
            var reply = Transport.Send(BridgeRequests.Json(writer =>
            {
                writer.WriteString("kind", "interactions");
                writer.WriteString("route", View.Route);
                writer.WriteString("presentation", Mount);
                writer.WriteNumber("generation", 1);
                writer.WriteStartArray("handlers");
                writer.WriteStartObject();
                writer.WriteString("name", "confirmDiscard");
                writer.WriteString("contract", Contract);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }), Connection, Client);
            using var document = JsonDocument.Parse(reply);
            Require(document.RootElement.TryGetProperty("registration", out var registration) && registration.GetString() == "ok",
                $"The confirmDiscard handler was not registered: {reply}");
        }

        public Task<string> WaitForPromptAsync() => NextPromptAsync(Transport, Connection, Mount, _seen);

        public void Answer(string prompt, bool discard)
        {
            using var request = JsonDocument.Parse(prompt);
            Require(Result(Transport.Send(ReplyJson(View.Route, Mount, request.RootElement, "confirmDiscard", Contract, discard),
                Connection, Client)) == "ok", "The web answer was rejected.");
        }
    }
}
