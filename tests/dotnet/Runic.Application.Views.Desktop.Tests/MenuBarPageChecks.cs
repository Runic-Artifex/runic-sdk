using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Menus;
using Runic.Desktop;
using Runic.Desktop.Internal;
using Runic.Platform;
using TUnit.Core;
using static DesktopChecks;

// B10 PR 4: the in-page menu bar (specs/desktop/menu-bar-page.md, protocol 2). A Page window's bar state reaches the page
// through the reserved Views route runic.menu-bar, which pushes each new state to a watching connection, follows
// CanExecute, and the page's invoke runs the item through DesktopWindow.TryExecuteMenuItemAsync: the window's commands,
// then the application's.
internal sealed class MenuBarPageChecks
{
    private static readonly IReadOnlyList<DesktopMenu> Bar =
    [
        new("File", [
            new DesktopMenuItem("New", "file.new") { Shortcut = DesktopShortcut.Parse("Primary+N") },
            new DesktopMenuItem("Save", "file.save") { Shortcut = DesktopShortcut.Parse("Primary+S"), AccessKey = 'S' },
        ]) { Role = DesktopMenuRole.File, AccessKey = 'F' },
    ];

    // The shared fixture: the state's menus for a declared bar on Linux, the counterpart of the npm controller's test.
    [Test, NotInParallel(Order = 41)]
    public async Task StateMatchesTheSharedFixture()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath()))!.AsObject();
        var bar = fixture["bar"]!.AsArray().Select(static menu => (DesktopMenu)Entry(menu!)).ToArray();
        var cannotExecute = fixture["cannotExecute"]!.AsArray().Select(static id => id!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var plan = DesktopMenuPlanner.PlanBar(bar, MenuPlatform.Linux);
        var json = MenuBarPageState.WriteMenus(plan, entry => entry.Enabled && !cannotExecute.Contains(entry.Item!.CommandId));
        var actual = JsonNode.Parse(json);
        Require(JsonNode.DeepEquals(actual, fixture["menus"]),
            $"The state's menus differ from specs/desktop/fixtures/menu-bar-page.json:\n{actual!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })}");

        var reply = JsonNode.Parse(MenuBarPageState.WriteReply(7, "page", "host", MenuPlatform.Linux, json))!.AsObject();
        var state = reply["state"]!.AsObject();
        Require(reply["ok"]!.GetValue<bool>() && state["kind"] is null && state["protocol"]!.GetValue<int>() == 2
            && state["revision"]!.GetValue<long>() == 7 && state["platform"]!.GetValue<string>() == "linux"
            && JsonNode.DeepEquals(state["menus"], fixture["menus"]), $"The state reply is wrong: {reply.ToJsonString()}");
        var push = JsonNode.Parse(MenuBarPageState.WritePush(7, "page", "app", MenuPlatform.Linux, json))!.AsObject();
        Require(push["kind"]!.GetValue<string>() == "menu-bar" && push["route"]!.GetValue<string>() == "runic.menu-bar"
            && push["state"]!["shortcuts"]!.GetValue<string>() == "app", $"The state push is wrong: {push.ToJsonString()}");
    }

    [Test, NotInParallel(Order = 42)]
    public async Task PageWindowStateReachesThePageAndInvokeRunsTheCommand()
    {
        var services = new ServiceCollection();
        services.AddScoped<MenuViewModel>();
        services.AddScoped<Func<IBridgeTransport, MenuViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        await using var provider = services.BuildServiceProvider();
        var factory = new NativeDispatchFactory { MenuBarPlacement = DesktopMenuBarPlacement.Page };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory, WaitForConnection = false, MenuBar = Bar,
        });
        var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded, MenuBarPresentation = DesktopMenuBarPresentation.Page },
        });
        var newCommand = new TestCommand();
        using var applicationMenu = host.UseApplicationMenu(new Dictionary<string, ICommand> { ["file.new"] = newCommand });
        var window = await host.OpenWindowAsync<MenuWindow>();
        using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
        var cancellation = timeout.Token;
        try
        {
            var viewModel = window.ViewModel;
            var presentation = ((DesktopBridgeWindow<MenuViewModel>)window.Host).Window;
            Require(presentation.MenuBarPlacement == DesktopMenuBarPlacement.Page, "The window did not report its bar in the page.");
            var native = factory.Hosts.Single();
            using var page = await DesktopWireClient.ConnectAsync(RunicDesktopHost.GetSurface(window)!, cancellation);

            // The state reaches the page once the window and its ViewModel's commands are bound.
            var state = await StateAsync(page, cancellation);
            while (Item(state, "file.save") is not { } bound || !bound["enabled"]!.GetValue<bool>())
                state = await WaitAsync(page, state, cancellation);
            Require(state["placement"]!.GetValue<string>() == "page" && state["shortcuts"]!.GetValue<string>() == "host",
                $"A Page window with a host key path reported {state.ToJsonString()}.");
            var save = Item(state, "file.save")!;
            Require(save["label"]!.GetValue<string>() == "Save" && save["accessKey"]!.GetValue<string>() == "S"
                && save["shortcut"]!.GetValue<string>() == "Primary+S" && save["ariaKeyShortcuts"] is not null,
                $"The Save item lost its declaration: {save.ToJsonString()}");

            // enabled follows CanExecuteChanged through a pending long poll.
            var waiting = WaitAsync(page, state, cancellation);
            viewModel.Save.Enabled = false;
            state = await waiting;
            while (Item(state, "file.save")!["enabled"]!.GetValue<bool>()) state = await WaitAsync(page, state, cancellation);
            var revision = state["revision"]!.GetValue<long>();
            var key = Item(state, "file.save")!["key"]!.GetValue<string>();
            Require(await InvokeAsync(page, revision, key, cancellation) == "disabled", "A disabled item ran.");
            Require(await InvokeAsync(page, revision, "9m/9i", cancellation) == "stale", "An unknown key was not stale.");
            Require(await InvokeAsync(page, revision + 1, key, cancellation) == "stale", "A future revision was not stale.");

            viewModel.Save.Enabled = true;
            while (!Item(state, "file.save")!["enabled"]!.GetValue<bool>()) state = await WaitAsync(page, state, cancellation);
            revision = state["revision"]!.GetValue<long>();
            Require(await InvokeAsync(page, revision, key, cancellation) == "executed", "The page could not run Save.");
            await viewModel.Save.Executed.Task.WaitAsync(cancellation);
            // The application's command runs through the same path as the native bar: the window's target cannot run it.
            var newKey = Item(state, "file.new")!["key"]!.GetValue<string>();
            Require(await InvokeAsync(page, revision, newKey, cancellation) == "executed", "The application's command did not run.");
            await newCommand.Executed.Task.WaitAsync(cancellation);

            // A new bar makes an earlier revision stale.
            desktop.SetMenuBar([.. Bar, new DesktopMenu("Help", [new DesktopMenuItem("About", "help.about")]) { Role = DesktopMenuRole.Help }]);
            while (Item(state, "help.about") is null) state = await WaitAsync(page, state, cancellation);
            Require(await InvokeAsync(page, revision, key, cancellation) == "stale", "An item of the previous bar was not stale.");

            // The "app" lease: the page matches the shortcuts, and the host's own path runs nothing until it ends.
            var chain = native.MenuCommands ?? throw new InvalidOperationException("The window host has no command path.");
            var saveItem = (DesktopMenuItem)desktop.MenuBar[0].Items[1];
            Require(chain.CanExecute(saveItem), "The host's own path could not run Save before the lease.");
            var leased = await ShortcutsAsync(page, "app", cancellation);
            Require(leased["shortcuts"]!.GetValue<string>() == "app", $"The lease was not reported: {leased.ToJsonString()}");
            Require(!chain.CanExecute(saveItem), "The host kept matching shortcuts under the page's lease.");
            revision = leased["revision"]!.GetValue<long>();
            key = Item(leased.AsObject(), "file.save")!["key"]!.GetValue<string>();
            Require(await InvokeAsync(page, revision, key, cancellation) == "executed", "The page's invoke stopped under its lease.");
            var invalid = await page.RequestAsync("{\"kind\":\"menu-bar-shortcuts\",\"route\":\"runic.menu-bar\",\"shortcuts\":\"none\"}", cancellation);
            Require(Reason(invalid) == "invalid-request", $"An unknown shortcut owner was answered {invalid}.");
            // menu-bar-unwatch ends the pushes; the next watch answers the current state again.
            Require(await page.RequestAsync("{\"kind\":\"menu-bar-unwatch\",\"route\":\"runic.menu-bar\"}", cancellation) == "{\"ok\":true}",
                "menu-bar-unwatch did not answer ok.");

            page.Dispose();
            await WaitUntilAsync(() => chain.CanExecute(saveItem), "The lease was not released with its connection.");
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    // An installed browser has no host key path: the page matches the shortcuts, and a Native window's bar is not the page's.
    [Test, NotInParallel(Order = 43)]
    public async Task WindowWithoutHostBarReportsPageShortcuts()
    {
        var services = new ServiceCollection();
        services.AddScoped<MenuViewModel>();
        services.AddScoped<Func<IBridgeTransport, MenuViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        await using var provider = services.BuildServiceProvider();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false, MenuBar = Bar,
        });
        foreach (var (presentation, placement) in new[] { (DesktopMenuBarPresentation.Page, "page"), (DesktopMenuBarPresentation.Native, "none") })
        {
            var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
            {
                SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded, MenuBarPresentation = presentation },
            });
            var window = await host.OpenWindowAsync<MenuWindow>();
            using var timeout = TestTimeouts.Cancellation(TimeSpan.FromSeconds(20));
            try
            {
                using var page = await DesktopWireClient.ConnectAsync(RunicDesktopHost.GetSurface(window)!, timeout.Token);
                var state = await StateAsync(page, timeout.Token);
                while (state["placement"]!.GetValue<string>() == "none" && placement != "none") state = await WaitAsync(page, state, timeout.Token);
                Require(state["placement"]!.GetValue<string>() == placement && state["shortcuts"]!.GetValue<string>() == "page",
                    $"A {presentation} window without a native bar reported {state.ToJsonString()}.");
                var leased = await ShortcutsAsync(page, "app", timeout.Token);
                Require(leased["shortcuts"]!.GetValue<string>() == (placement == "page" ? "app" : "page"),
                    $"The lease applies only while the bar is in the page: {leased.ToJsonString()}");
                // A revision that is not a number, a kind the route does not have, and a menu bar kind on a View's route
                // are invalid requests, not failed calls.
                foreach (var request in new[]
                {
                    "{\"kind\":\"menu-bar-invoke\",\"route\":\"runic.menu-bar\",\"revision\":\"1\",\"key\":\"0m/0i\"}",
                    "{\"kind\":\"menu-bar-wait\",\"route\":\"runic.menu-bar\",\"after\":1}",
                    "{\"kind\":\"snapshot\",\"route\":\"runic.menu-bar\"}",
                    "{\"kind\":\"menu-bar-watch\",\"route\":\"menu\"}",
                })
                {
                    var reply = await page.RequestAsync(request, timeout.Token);
                    Require(Reason(reply) == "invalid-request", $"{request} was answered {reply}.");
                }
            }
            finally
            {
                await window.DisposeAsync();
            }
        }
    }

    // Completes the hello and watches the bar: the reply's state, and later states as pushes.
    private static async Task<JsonObject> StateAsync(DesktopWireClient page, CancellationToken cancellationToken)
    {
        await page.HelloAsync(cancellationToken);
        var reply = await page.RequestAsync("{\"kind\":\"menu-bar-watch\",\"route\":\"runic.menu-bar\"}", cancellationToken);
        var state = JsonNode.Parse(reply)!["state"]?.AsObject();
        Require(state is not null && state["protocol"]!.GetValue<int>() == 2, $"menu-bar-watch answered {reply}.");
        return state!;
    }

    // The next state the watch pushes; its revision is higher than the one before.
    private static async Task<JsonObject> WaitAsync(DesktopWireClient page, JsonObject after, CancellationToken cancellationToken)
    {
        while (true)
        {
            var push = JsonNode.Parse(await page.NextPushAsync(cancellationToken))!.AsObject();
            if (push["kind"]?.GetValue<string>() != "menu-bar") continue;
            Require(push["route"]!.GetValue<string>() == "runic.menu-bar", $"The push names another route: {push.ToJsonString()}");
            var state = push["state"]!.AsObject();
            if (state["revision"]!.GetValue<long>() > after["revision"]!.GetValue<long>()) return state;
        }
    }

    private static async Task<string> InvokeAsync(DesktopWireClient page, long revision, string key, CancellationToken cancellationToken)
    {
        var reply = await page.RequestAsync(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "menu-bar-invoke", ["route"] = "runic.menu-bar", ["revision"] = revision, ["key"] = key,
        }), cancellationToken);
        return JsonNode.Parse(reply)!["result"]!.GetValue<string>();
    }

    private static async Task<JsonObject> ShortcutsAsync(DesktopWireClient page, string owner, CancellationToken cancellationToken)
    {
        var reply = await page.RequestAsync(
            $"{{\"kind\":\"menu-bar-shortcuts\",\"route\":\"runic.menu-bar\",\"shortcuts\":\"{owner}\"}}", cancellationToken);
        return JsonNode.Parse(reply)!["state"]!.AsObject();
    }

    // The reason of a Views failure reply, if the reply is one.
    private static string? Reason(string reply) =>
        JsonNode.Parse(reply) is JsonObject { } envelope && envelope["ok"]?.GetValue<bool>() == false
            ? envelope["error"]?["reason"]?.GetValue<string>() : null;

    private static JsonObject? Item(JsonObject state, string commandId)
    {
        static JsonObject? Find(JsonArray entries, string commandId)
        {
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (entry["kind"]!.GetValue<string>() == "item" && entry["commandId"]!.GetValue<string>() == commandId) return entry;
                if (entry["entries"] is JsonArray children && Find(children, commandId) is { } found) return found;
            }
            return null;
        }
        return Find(state["menus"]!.AsArray(), commandId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            if (waited > 5000) throw new InvalidOperationException(message);
            await Task.Delay(20);
        }
    }

    // A declared entry of the fixture.
    private static DesktopMenuEntry Entry(JsonNode node)
    {
        var entry = node.AsObject();
        if (entry["separator"] is not null) return new DesktopMenuSeparator();
        static char? Key(JsonObject entry) => entry["accessKey"]?.GetValue<string>()[0];
        if (entry["menu"] is { } title)
        {
            return new DesktopMenu(title.GetValue<string>(), [.. entry["items"]!.AsArray().Select(static child => Entry(child!))])
            {
                Role = entry["role"]?.GetValue<string>() switch
                {
                    "file" => DesktopMenuRole.File,
                    "help" => DesktopMenuRole.Help,
                    "application" => DesktopMenuRole.Application,
                    _ => DesktopMenuRole.None,
                },
                AccessKey = Key(entry),
                IsEnabled = entry["enabled"]?.GetValue<bool>() ?? true,
            };
        }
        return new DesktopMenuItem(entry["item"]!.GetValue<string>(), entry["commandId"]!.GetValue<string>())
        {
            AccessKey = Key(entry),
            Shortcut = entry["shortcut"] is { } shortcut ? DesktopShortcut.Parse(shortcut.GetValue<string>()) : null,
            CommandParameter = entry["commandParameter"]?.GetValue<string>(),
            RadioGroup = entry["radioGroup"]?.GetValue<string>(),
            IsChecked = entry["checked"]?.GetValue<bool>(),
            IsEnabled = entry["enabled"]?.GetValue<bool>() ?? true,
        };
    }

    private static string FixturePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "specs", "desktop", "fixtures", "menu-bar-page.json");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("specs/desktop/fixtures/menu-bar-page.json was not found above the test's directory.");
    }
}
