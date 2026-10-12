using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
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
        using var transportA = new InMemoryViewTransport();
        using var transportB = new InMemoryViewTransport();
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
        Require(transportA.Call($"{route}Mount", new(StringValue: "a:present", ClientKey: Client, ConnectionKey: Connection)) == "ok",
            "Window A's presentation did not mount.");
        var waitA = transportA.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: JsonSerializer.Serialize(new
        {
            route, presentationId = "a:present", handlers = new[] { new { name = "confirm", contract = "testing.confirm.v1" } },
        }), ClientKey: Client, ConnectionKey: Connection)).AsTask();
        await Task.Yield();

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
        Require(Kind(transportA.Call(BridgeInteractionRouter.ReplyRoute, new(StringValue: JsonSerializer.Serialize(new
        {
            kind = "answered", requestId = root.GetProperty("requestId").GetString(), route, presentationId = "a:present",
            ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(), name = "confirm", contract = "testing.confirm.v1", output = true,
        }), ClientKey: Client, ConnectionKey: Connection))) == "ok" && await answer.WaitAsync(Timeout),
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
        Require(root.GetProperty("kind").GetString() == "request"
            && root.GetProperty("input").GetProperty("title").GetString() == "reactive editor"
            && root.GetProperty("input").GetProperty("draft").GetString() == "changed",
            $"The web View did not receive the typed leave prompt: {root}.");
        return json;
    }

    private static string Kind(string json)
    {
        using var reply = JsonDocument.Parse(json);
        return reply.RootElement.GetProperty("kind").GetString()!;
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
        private InMemoryViewTransport Transport => Native.Host.Transport;

        public static async Task<GuardedShell> OpenAsync()
        {
            Require(RunicReactiveSchedulerProvider.InstallMainThreadScheduler(),
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
            var presentation = new EditorPresentation(Transport, (ReactiveNavEditorViewModel)ViewModel.Main.Current!, view,
                view.Mount(clientKey: Client, connectionKey: Connection), Client, Connection);
            view.Set(vm => vm.Draft, "changed");
            return presentation;
        }

        // The shell's Back, started by the browser client on the connection; returns the settled outcome.
        public async Task<string> BackAsync(string client, string connection)
        {
            var requestId = $"back-{Interlocked.Increment(ref _requests)}";
            string operation;
            using (var admission = JsonDocument.Parse(Transport.Call("reactiveNavShellStartBack",
                new(StringValue: requestId, ClientKey: client, ConnectionKey: connection))))
            {
                Require(admission.RootElement.GetProperty("kind").GetString() == "accepted",
                    $"The web Back command was not admitted: {admission.RootElement}.");
                operation = admission.RootElement.GetProperty("contract").GetString()!;
            }
            using var completion = JsonDocument.Parse(await Transport.CallAsync("__runicOperationWait",
                new(StringValue: JsonSerializer.Serialize(new { contract = operation, requestId }))));
            var status = completion.RootElement;
            if (status.GetProperty("kind").GetString() != "succeeded") return status.GetProperty("kind").GetString()!;
            var result = status.GetProperty("result");
            return result.GetProperty("kind").GetString()!.ToLowerInvariant()
                + (result.GetProperty("rejection").ValueKind == JsonValueKind.Null ? ""
                    : ":" + result.GetProperty("rejection").GetString()!.ToLowerInvariant());
        }

        public async ValueTask DisposeAsync()
        {
            await Window.DisposeAsync();
            await _provider.DisposeAsync();
        }
    }

    // The browser side of one mounted editor View: it waits for the guard's question and answers it.
    private sealed record EditorPresentation(InMemoryViewTransport Transport, ReactiveNavEditorViewModel ViewModel,
        RunicViewDriver<ReactiveNavEditorViewModel> View, string Mount, string Client, string Connection)
    {
        public EditorPresentation MountAgain(string client, string connection) =>
            this with { Mount = View.Mount(clientKey: client, connectionKey: connection), Client = client, Connection = connection };

        public Task<string> WaitForPromptAsync()
        {
            var wait = Transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: JsonSerializer.Serialize(new
            {
                route = View.Route,
                presentationId = Mount,
                handlers = new[] { new { name = "confirmDiscard", contract = Contract } },
            }), ClientKey: Client, ConnectionKey: Connection)).AsTask();
            return wait;
        }

        public void Answer(string prompt, bool discard)
        {
            using var request = JsonDocument.Parse(prompt);
            var root = request.RootElement;
            var reply = JsonSerializer.Serialize(new
            {
                kind = "answered",
                requestId = root.GetProperty("requestId").GetString(),
                route = View.Route,
                presentationId = Mount,
                ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
                name = "confirmDiscard",
                contract = Contract,
                output = discard,
            });
            Require(Kind(Transport.Call(BridgeInteractionRouter.ReplyRoute,
                new(StringValue: reply, ClientKey: Client, ConnectionKey: Connection))) == "ok", "The web answer was rejected.");
        }
    }
}

// Opens Windows on RunicWindowTestHost, so a test drives a Window's routes as its browser does. It has no close request
// of its own: RequestCloseAsync asks the Window's close guard and then closes it, and UserCloseAsync asks the guard as
// Runic Desktop's close button does.
internal sealed class TestWindowHost(IServiceProvider services) : IRunicWindowHost
{
    public IServiceProvider Services { get; } = services;

    public IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options) => [];

    public async ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening,
        CancellationToken cancellationToken) where TViewModel : class
    {
        try
        {
            var host = new RunicWindowTestHost<TViewModel>(opening.ViewModel,
                (transport, content, _) => opening.AttachBridge(transport, content),
                new RunicWindowTestHostOptions
                {
                    ViewLocator = new NavViewLocator(),
                    ModelContext = opening.Scope.ServiceProvider.GetRequiredService<IRunicModelContext>(),
                });
            return new TestWindow<TViewModel>(opening.Scope, host, opening.CloseGuard);
        }
        catch
        {
            await opening.Scope.DisposeAsync();
            throw;
        }
    }
}

internal sealed class TestWindow<TViewModel>(AsyncServiceScope scope, RunicWindowTestHost<TViewModel> host,
    Func<CancellationToken, ValueTask<bool>>? closeGuard) : IBridgeWindow where TViewModel : class
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<BridgeWindowCloseResult>? _closing;

    public RunicWindowTestHost<TViewModel> Host { get; } = host;
    public bool IsOpen => !_closed.Task.IsCompleted;

    // The user's close button: the Window's close guard decides, then the window closes.
    public async Task<bool> UserCloseAsync()
    {
        if (closeGuard is not null && !await closeGuard(CancellationToken.None)) return false;
        await CloseAsync(TimeSpan.FromSeconds(5));
        return true;
    }

    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        lock (_gate) _closing ??= Task.Run(() => CloseCoreAsync(timeout));
        return new(_closing.WaitAsync(cancellationToken));
    }

    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        new(_closed.Task.WaitAsync(cancellationToken));

    public async ValueTask DisposeAsync() => await CloseAsync(TimeSpan.Zero);

    private async Task<BridgeWindowCloseResult> CloseCoreAsync(TimeSpan timeout)
    {
        _closed.TrySetResult();
        var result = await Host.BeginCloseAsync(timeout);
        Host.Dispose();
        await scope.DisposeAsync();
        return result;
    }
}
