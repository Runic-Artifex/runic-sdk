extern alias cswebui;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Platform;
using TUnit.Core;
using WebUiMenuBar = cswebui::Runic.Application.Views.CsWebUi.CsWebUiMenuBar;
using WebUiMenuBarPageWindow = cswebui::Runic.Application.Views.CsWebUi.CsWebUiMenuBarPageWindow;
using WebUiMenuBarRoutes = cswebui::Runic.Application.Views.Menus.MenuBarPageRoutes;
using WebUiMenuCommands = cswebui::Runic.Application.Views.Menus.MenuCommandBinding;

// A menu item that a page's in-page bar invokes runs in a command invocation with that page's client and connection
// (specs/application, "Interactions", Scope): in a CS-WebUI window that two browsers share, the command's view requests
// and questions reach only the page that clicked, never the other one.
internal sealed class MenuBarPageIdentityChecks
{
    private const string Route = "identity";
    private const string FocusContract = "testing.identity.focus";
    private const string ConfirmContract = "testing.identity.confirm";

    [Test]
    public async Task APagesMenuClickReachesOnlyThatPage()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        using var connections = new RunicConnections();
        var model = new IdentityViewModel { Connections = connections };
        using var bridge = new IdentityBridge(transport, model, session);
        connections.Bind(session);
        using var presentations = session.AttachRootInteractionPresentation(Route);
        var bar = new WebUiMenuBar([new DesktopMenu("File", [new DesktopMenuItem("Focus", "file.focus")])]);
        using var routes = new WebUiMenuBarRoutes(bar, transport, NullLogger.Instance);
        using var commands = await WebUiMenuCommands.BindAsync(bar, session.ModelContext, model.GetMenuCommand, NullLogger.Instance, window: session);
        routes.Attach(new WebUiMenuBarPageWindow(bar, commands), commands);

        // Two browsers show the window; each page handles the request and the question.
        foreach (var (connection, client, presentation) in new[] { ("connection-a", "client-a", "page-a"), ("connection-b", "client-b", "page-b") })
        {
            Send(transport, $$"""{"kind":"hello","protocol":4,"session":"s-{{connection}}","client":{"name":"t","version":"1"},"features":["view-requests/1"]}""", connection, client);
            var opened = Send(transport, $$"""{"kind":"open","route":"{{Route}}","presentation":"{{presentation}}","handlers":[{"name":"confirm","contract":"{{ConfirmContract}}"}]}""", connection, client);
            Require(opened["registration"]?.GetValue<string>() == "ok", $"The page did not open: {opened}");
            var registered = Send(transport, $$"""{"kind":"view-requests","route":"{{Route}}","presentation":"{{presentation}}","generation":1,"requests":[{"name":"focus","contract":"{{FocusContract}}"}]}""", connection, client);
            Require(registered["registration"]?.GetValue<string>() == "ok", $"The page did not register its requests: {registered}");
        }

        // Page b clicks the item in its in-page bar.
        var (revision, key) = await EnabledItemAsync(transport, "connection-b", "client-b", "file.focus");
        var invoked = Send(transport, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "menu-bar-invoke", ["route"] = "runic.menu-bar", ["revision"] = revision, ["key"] = key,
        }), "connection-b", "client-b");
        Require(invoked["result"]?.GetValue<string>() == "executed", $"The page's click did not run the item: {invoked}");

        var focus = await NextPushAsync(transport, "view-request");
        Require(focus.Connection == "connection-b" && focus.Json["presentation"]!.GetValue<string>() == "page-b",
            $"The command's view request did not reach only the page that clicked: {focus.Json}");
        var question = await NextPushAsync(transport, "interaction");
        Require(question.Connection == "connection-b" && question.Json["presentation"]!.GetValue<string>() == "page-b",
            $"The command's question did not reach only the page that clicked: {question.Json}");
        Send(transport, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "interaction-reply", ["route"] = Route, ["presentation"] = "page-b",
            ["requestId"] = question.Json["requestId"]!.GetValue<string>(), ["ownerEpoch"] = question.Json["ownerEpoch"]!.GetValue<long>(),
            ["name"] = "confirm", ["contract"] = ConfirmContract, ["outcome"] = "answered", ["output"] = true,
        }), "connection-b", "client-b");
        Require(await model.Answered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)), "Page b's answer did not reach the ViewModel.");
        // IRunicConnections lists both browsers, and the command knows that page b clicked (D4).
        // The window's own keys of the two connections, which no later connection reuses.
        var dispatcher = session.Dispatcher;
        var pageA = new RunicConnection(dispatcher.ConnectionKeyOf("connection-a")!, "client-a");
        var pageB = new RunicConnection(dispatcher.ConnectionKeyOf("connection-b")!, "client-b");
        Require(model.Caller == pageB, $"The menu command's caller was {model.Caller}.");
        Require(connections.Current.SequenceEqual([pageA, pageB]),
            $"The window did not list both browsers: {string.Join(", ", connections.Current)}");
        Require(transport.TakeMessages().All(message => message.ConnectionKey != "connection-a" || message.Kind is not ("view-request" or "interaction")),
            "Page a received the command of page b's click.");
    }

    // An async CommunityToolkit command that a page's in-page menu click runs keeps that page's identity until its task
    // ends: its question after an await reaches the page that clicked, in a window two browsers share.
    [Test]
    public async Task AnAsyncInPageMenuCommandAsksTheClickingPageAfterAnAwait()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        var model = new IdentityViewModel();
        using var bridge = new IdentityBridge(transport, model, session);
        using var presentations = session.AttachRootInteractionPresentation(Route);
        var bar = new WebUiMenuBar([new DesktopMenu("File", [new DesktopMenuItem("Later", "file.later")])]);
        using var routes = new WebUiMenuBarRoutes(bar, transport, NullLogger.Instance);
        using var commands = await WebUiMenuCommands.BindAsync(bar, session.ModelContext, model.GetMenuCommand, NullLogger.Instance, window: session);
        routes.Attach(new WebUiMenuBarPageWindow(bar, commands), commands);
        foreach (var (connection, client, presentation) in new[] { ("connection-a", "client-a", "page-a"), ("connection-b", "client-b", "page-b") })
        {
            Send(transport, $$$"""{"kind":"hello","protocol":4,"session":"s-{{{connection}}}","client":{"name":"t","version":"1"}}""", connection, client);
            var opened = Send(transport, $$"""{"kind":"open","route":"{{Route}}","presentation":"{{presentation}}","handlers":[{"name":"confirm","contract":"{{ConfirmContract}}"}]}""", connection, client);
            Require(opened["registration"]?.GetValue<string>() == "ok", $"The page did not open: {opened}");
        }

        var (revision, key) = await EnabledItemAsync(transport, "connection-b", "client-b", "file.later");
        var invoked = Send(transport, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "menu-bar-invoke", ["route"] = "runic.menu-bar", ["revision"] = revision, ["key"] = key,
        }), "connection-b", "client-b");
        Require(invoked["result"]?.GetValue<string>() == "executed", $"The page's click did not run the item: {invoked}");
        var question = await NextPushAsync(transport, "interaction");
        Require(question.Connection == "connection-b" && question.Json["presentation"]!.GetValue<string>() == "page-b"
            && question.Json["input"]!.GetValue<string>() == "Later?",
            $"The async command's question after an await did not reach the page that clicked: {question.Json}");
        Send(transport, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "interaction-reply", ["route"] = Route, ["presentation"] = "page-b",
            ["requestId"] = question.Json["requestId"]!.GetValue<string>(), ["ownerEpoch"] = question.Json["ownerEpoch"]!.GetValue<long>(),
            ["name"] = "confirm", ["contract"] = ConfirmContract, ["outcome"] = "answered", ["output"] = true,
        }), "connection-b", "client-b");
        Require(await model.LaterAnswered.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)), "Page b's answer did not reach the command.");
        // Once the task ended, the invocation ended with it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (model.LaterInvocation is not { IsEnded: true } && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(model.LaterInvocation is { IsEnded: true }, "The command's invocation outlived its task.");
    }

    // The native menu bar and host shortcuts run in the window scope, which names no page: it flows with the command's
    // asynchronous work, so an async command that asks after its first await still reaches the window's one page.
    [Test]
    public async Task AnAsyncNativeMenuCommandAsksTheWindowsPageAfterAnAwait()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        var model = new IdentityViewModel();
        using var bridge = new IdentityBridge(transport, model, session);
        using var presentations = session.AttachRootInteractionPresentation(Route);
        var bar = new WebUiMenuBar([new DesktopMenu("File", [new DesktopMenuItem("Ask", "file.ask")])]);
        using var commands = await WebUiMenuCommands.BindAsync(bar, session.ModelContext, model.GetMenuCommand, NullLogger.Instance, window: session);

        Send(transport, """{"kind":"hello","protocol":4,"session":"s-a","client":{"name":"t","version":"1"}}""", "connection-a", "client-a");
        var opened = Send(transport, $$"""{"kind":"open","route":"{{Route}}","presentation":"page-a","handlers":[{"name":"confirm","contract":"{{ConfirmContract}}"}]}""",
            "connection-a", "client-a");
        Require(opened["registration"]?.GetValue<string>() == "ok", $"The page did not open: {opened}");

        // The native bar: no page clicked, so no connection identity.
        commands.Execute(new DesktopMenuItem("Ask", "file.ask"));
        var question = await NextPushAsync(transport, "interaction");
        Require(question.Connection == "connection-a" && question.Json["presentation"]!.GetValue<string>() == "page-a"
            && question.Json["input"]!.GetValue<string>() == "Ask after an await?",
            $"The async native menu command's question did not reach the window's page: {question.Json}");
        Send(transport, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "interaction-reply", ["route"] = Route, ["presentation"] = "page-a",
            ["requestId"] = question.Json["requestId"]!.GetValue<string>(), ["ownerEpoch"] = question.Json["ownerEpoch"]!.GetValue<long>(),
            ["name"] = "confirm", ["contract"] = ConfirmContract, ["outcome"] = "answered", ["output"] = true,
        }), "connection-a", "client-a");
        Require(await model.AskedAfterAwait.Task.WaitScaledAsync(TimeSpan.FromSeconds(5)), "The page's answer did not reach the command.");
    }

    // FlowCultureFromLocale (S6): a native menu command runs in the culture of the window's locale, through its awaits.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ANativeMenuCommandRunsInTheWindowsCultureWhenItOptsIn(bool flow)
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport,
            new WindowContentSessionOptions { IncludeFailureDetail = false, FlowCultureFromLocale = flow });
        session.Locale = "fr-CA";
        var model = new IdentityViewModel();
        using var bridge = new IdentityBridge(transport, model, session);
        var bar = new WebUiMenuBar([new DesktopMenu("File", [new DesktopMenuItem("Culture", "file.culture")])]);
        using var commands = await WebUiMenuCommands.BindAsync(bar, session.ModelContext, model.GetMenuCommand, NullLogger.Instance, window: session);
        var process = $"{System.Globalization.CultureInfo.CurrentCulture.Name}/{System.Globalization.CultureInfo.CurrentUICulture.Name}";
        commands.Execute(new DesktopMenuItem("Culture", "file.culture"));
        var seen = await model.CultureAfterAwait.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(seen == (flow ? "fr-CA/fr-CA" : process), $"The menu command ran in {seen}.");
    }

    private static JsonObject Send(InMemoryBridgeTransport transport, string json, string connection, string client) =>
        JsonNode.Parse(transport.Request(json, connection, client))!.AsObject();

    // The revision and key of an enabled item in the bar's state, as the page reads them with menu-bar-watch.
    private static async Task<(long Revision, string Key)> EnabledItemAsync(InMemoryBridgeTransport transport, string connection,
        string client, string commandId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var state = Send(transport, """{"kind":"menu-bar-watch","route":"runic.menu-bar"}""", connection, client)["state"]!.AsObject();
            var item = state["menus"]!.AsArray().OfType<JsonObject>().SelectMany(menu => menu["entries"]!.AsArray().OfType<JsonObject>())
                .FirstOrDefault(entry => entry["commandId"]?.GetValue<string>() == commandId);
            if (item?["enabled"]?.GetValue<bool>() == true) return (state["revision"]!.GetValue<long>(), item["key"]!.GetValue<string>());
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"The item {commandId} never became enabled: {state}");
            await Task.Delay(20);
        }
    }

    private static async Task<(string Connection, JsonObject Json)> NextPushAsync(InMemoryBridgeTransport transport, string kind)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            if (transport.Messages.FirstOrDefault(message => message.Kind == kind) is { ConnectionKey: { } connection } found)
                return (connection, JsonNode.Parse(found.Json)!.AsObject());
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No {kind} push.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record IdentityFocus(string Member);

    private sealed class IdentityViewModel : INotifyPropertyChanged, IRunicMenuCommandSource
    {
        public IdentityViewModel() => FocusCommand = new MenuCommand(async () =>
        {
            Caller = Connections?.Caller;
            Focus.Raise(new IdentityFocus("title"));
            Answered.TrySetResult(await Confirm.AskAsync("Focus?"));
        });

        public ViewRequest<IdentityFocus> Focus { get; } = new();
        public ViewQuestion<string, bool> Confirm { get; } = new((_, _) => ValueTask.FromResult(false));
        public ICommand FocusCommand { get; }
        public RunicConnections? Connections { get; init; }
        public RunicConnection? Caller { get; private set; }
        public TaskCompletionSource<bool> Answered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public TaskCompletionSource<bool> AskedAfterAwait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // An async command whose question comes after its first await, when ICommand.Execute has long returned.
        public ICommand AskCommand => new MenuCommand(async () =>
        {
            await Task.Delay(20);
            AskedAfterAwait.TrySetResult(await Confirm.AskAsync("Ask after an await?"));
        });

        public TaskCompletionSource<string> CultureAfterAwait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // FlowCultureFromLocale: the culture an async menu command sees after its first await.
        public ICommand CultureCommand => new MenuCommand(async () =>
        {
            await Task.Delay(20);
            CultureAfterAwait.TrySetResult($"{System.Globalization.CultureInfo.CurrentCulture.Name}/{System.Globalization.CultureInfo.CurrentUICulture.Name}");
        });

        public TaskCompletionSource<bool> LaterAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RunicInteractionInvocation? LaterInvocation { get; private set; }

        // A CommunityToolkit async command: ICommand.Execute returns at its first await, its ExecutionTask runs on.
        public ICommand LaterCommand => _later ??= new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () =>
        {
            LaterInvocation = RunicInteractionInvocation.Current;
            await Task.Delay(20);
            LaterAnswered.TrySetResult(await Confirm.AskAsync("Later?"));
        });
        private ICommand? _later;

        public ICommand? GetMenuCommand(string commandId) => commandId switch
        {
            "file.focus" => FocusCommand,
            "file.ask" => AskCommand,
            "file.later" => LaterCommand,
            "file.culture" => CultureCommand,
            _ => null,
        };
    }

    private sealed class MenuCommand(Func<Task> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _ = execute();
    }

    private sealed class IdentityBridge(IBridgeTransport transport, IdentityViewModel model, WindowContentSession session)
        : ViewModelBridge<IdentityViewModel>(transport, model, Route, [new("ready", (writer, _) => writer.WriteBooleanValue(true))],
            [], [], [], content: session,
            interactions:
            [
                ViewRequestDescriptor.Create<IdentityViewModel, IdentityFocus>("focus", FocusContract, value => value.Focus,
                    static input => JsonSerializer.Serialize(new { member = input.Member })),
                ViewQuestionDescriptor.Create<IdentityViewModel, string, bool>("confirm", ConfirmContract, value => value.Confirm,
                    static input => JsonSerializer.Serialize(input), static output => output.GetBoolean()),
            ]);
}
