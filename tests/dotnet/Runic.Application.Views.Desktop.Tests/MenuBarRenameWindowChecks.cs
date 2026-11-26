extern alias cswebui;
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

// The rename window (specs/application/README.md, "Rename window"; specs/desktop/menu-bar-page.md): another release's
// page, which names the menu bar's route rax.menu-bar, reaches the same bar as today's runic.menu-bar. The host serves
// both names with one state and pushes to each connection under the name it watched with. A rax.* route that nothing
// serves answers released, as a runic.* one does.
internal sealed class MenuBarRenameWindowChecks
{
    [Test]
    public async Task BothNamesOfTheMenuBarRouteReachOneBar()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        var bar = new WebUiMenuBar([new DesktopMenu("File", [new DesktopMenuItem("New", "file.new")])]);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new CountingCommand(() => executed.TrySetResult());
        using var routes = new WebUiMenuBarRoutes(bar, transport, NullLogger.Instance);
        using var commands = await WebUiMenuCommands.BindAsync(bar, session.ModelContext, id => id == "file.new" ? command : null,
            NullLogger.Instance, window: session);
        routes.Attach(new WebUiMenuBarPageWindow(bar, commands), commands);
        foreach (var connection in new[] { "today", "renamed", "other" })
            Send(transport, $$$"""{"kind":"hello","protocol":4,"session":"s-{{{connection}}}","client":{"name":"t","version":"1"}}""", connection);

        var today = Send(transport, """{"kind":"menu-bar-watch","route":"runic.menu-bar"}""", "today");
        var renamed = Send(transport, """{"kind":"menu-bar-watch","route":"rax.menu-bar"}""", "renamed");
        Require(today["ok"]!.GetValue<bool>() && renamed["ok"]!.GetValue<bool>()
            && JsonNode.DeepEquals(today["state"]!["menus"], renamed["state"]!["menus"])
            && today["state"]!["revision"]!.GetValue<long>() == renamed["state"]!["revision"]!.GetValue<long>(),
            $"The two names answered different states: {today} and {renamed}");

        // A third page takes the shortcut lease under the renamed name: a new state, pushed to both watchers, each under
        // the name it watched with.
        transport.TakeMessages();
        var leased = Send(transport, """{"kind":"menu-bar-shortcuts","route":"rax.menu-bar","shortcuts":"app"}""", "other");
        Require(leased["ok"]!.GetValue<bool>(), $"The renamed route refused the lease: {leased}");
        var pushes = await PushesAsync(transport, 2);
        Require(pushes.TryGetValue("today", out var todayPush) && todayPush["route"]!.GetValue<string>() == "runic.menu-bar"
            && pushes.TryGetValue("renamed", out var renamedPush) && renamedPush["route"]!.GetValue<string>() == "rax.menu-bar"
            && !pushes.ContainsKey("other"),
            $"The pushes did not follow the watched names: {string.Join(", ", pushes.Select(push => $"{push.Key}: {push.Value}"))}");

        // An invoke under the renamed name runs the item.
        var state = renamed["state"]!.AsObject();
        var key = state["menus"]!.AsArray().OfType<JsonObject>().SelectMany(menu => menu["entries"]!.AsArray().OfType<JsonObject>())
            .Single(entry => entry["commandId"]?.GetValue<string>() == "file.new")["key"]!.GetValue<string>();
        var invoked = Send(transport, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["kind"] = "menu-bar-invoke", ["route"] = "rax.menu-bar", ["revision"] = todayPush!["state"]!["revision"]!.GetValue<long>(), ["key"] = key,
        }), "renamed");
        Require(invoked["result"]?.GetValue<string>() == "executed", $"The renamed route did not run the item: {invoked}");
        await executed.Task.WaitScaledAsync(TimeSpan.FromSeconds(5));

        // Unwatching one name ends only that connection's watch.
        Require(Send(transport, """{"kind":"menu-bar-unwatch","route":"rax.menu-bar"}""", "renamed")["ok"]!.GetValue<bool>(), "The unwatch failed.");
        transport.TakeMessages();
        Send(transport, """{"kind":"menu-bar-shortcuts","route":"runic.menu-bar","shortcuts":"runic"}""", "other");
        var after = await PushesAsync(transport, 1);
        Require(after.Keys.SequenceEqual(["today"]), $"An unwatched connection was pushed: {string.Join(", ", after.Keys)}");
    }

    [Test]
    public async Task AReservedRouteOfEitherPrefixThatNothingServesIsReleased()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        Send(transport, """{"kind":"hello","protocol":4,"session":"s-1","client":{"name":"t","version":"1"}}""");
        foreach (var route in new[] { "runic.other", "rax.other", "rax.menu-bar" })
        {
            var reply = Send(transport, $$"""{"kind":"snapshot","route":"{{route}}"}""");
            Require(reply["error"]?["kind"]?.GetValue<string>() == "released", $"{route}: {reply}");
        }
        foreach (var route in new[] { "rax.", "raxother.x", "Rax.other" })
        {
            var reply = Send(transport, $$"""{"kind":"snapshot","route":"{{route}}"}""");
            Require(reply["error"]?["reason"]?.GetValue<string>() == "invalid-request", $"{route}: {reply}");
        }
        await Task.CompletedTask;
    }

    private static JsonObject Send(InMemoryBridgeTransport transport, string json, string connection = InMemoryBridgeTransport.DefaultConnectionKey) =>
        JsonNode.Parse(transport.Request(json, connection, connection))!.AsObject();

    // The menu-bar pushes of the next count connections, by connection.
    private static async Task<Dictionary<string, JsonObject>> PushesAsync(InMemoryBridgeTransport transport, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var pushes = transport.Messages.Where(message => message.Kind == "menu-bar" && message.ConnectionKey is not null)
                .GroupBy(message => message.ConnectionKey!)
                .ToDictionary(group => group.Key, group => JsonNode.Parse(group.Last().Json)!.AsObject());
            if (pushes.Count >= count)
            {
                // Pushes that a wrong implementation would add arrive in the same delivery.
                await Task.Delay(50);
                return transport.Messages.Where(message => message.Kind == "menu-bar" && message.ConnectionKey is not null)
                    .GroupBy(message => message.ConnectionKey!)
                    .ToDictionary(group => group.Key, group => JsonNode.Parse(group.Last().Json)!.AsObject());
            }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Only {pushes.Count} of {count} menu-bar pushes arrived.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
