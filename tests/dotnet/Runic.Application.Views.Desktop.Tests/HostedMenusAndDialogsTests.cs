using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.Desktop.Hosting;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;
using static HostedApplicationTests;

// menus/1 and dialogs/1 (host-protocol.md, Menus and Dialogs) between the host of hosted applications and real guest
// processes (HostedGuestProgram "menus", "services", "dialog-closed", "dialog-crash"). The host's windows are headless:
// HostedWindowHost is their native menu bar and context menu, FakeHostDialogs their dialogs.
internal sealed class HostedMenusAndDialogsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static bool HasItem(IReadOnlyList<DesktopMenu> bar, string menu, string item) =>
        bar.FirstOrDefault(entry => entry.Title == menu)?.Items.OfType<DesktopMenuItem>().Any(entry => entry.Title == item) == true;

    private static DesktopMenuItem? Find(IReadOnlyList<DesktopMenu> bar, params string[] titles)
    {
        IEnumerable<DesktopMenuEntry> entries = bar;
        DesktopMenuEntry? found = null;
        foreach (var title in titles)
        {
            found = entries.FirstOrDefault(entry => entry is DesktopMenu menu && menu.Title == title || entry is DesktopMenuItem item && item.Title == title);
            entries = found is DesktopMenu menu ? menu.Items : [];
        }
        return found as DesktopMenuItem;
    }

    [Test, NotInParallel(Order = 60)]
    public async Task TheGuestsMenuBarShowsInItsWindowAndRunsTheGuestsCommands()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        // The host application's own bar never shows in a guest's window.
        fixture.Desktop.SetMenuBar([new DesktopMenu("Host", [new DesktopMenuItem("Host item", "host.item")])]);
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("menus"));
        var window = await opened.WaitScaledAsync(Timeout);
        var native = fixture.Factory.Hosts.Single();
        var bar = await native.WaitForMenuBarAsync(menus => HasItem(menus, "File", "Open…"), Timeout);
        Check(bar.Count == 2 && bar[0].Role == DesktopMenuRole.File && bar[1].Title == "View", $"The window shows {HostedWindowHost.Describe(bar)}.");
        Check(ReferenceEquals(window.MenuBar, bar) || HostedWindowHost.Describe(window.MenuBar) == HostedWindowHost.Describe(bar),
            "RunicHostedWindow.MenuBar is not the bar the window shows.");
        var open = Find(bar, "File", "Open…")!;
        Check(open.IsEnabled && open.Shortcut == new DesktopShortcut("O") && open.IsChecked is null, "Open… lost its state or shortcut.");
        Check(open.CommandId == RunicHostedMenuRequest.GuestCommandId && RunicHostedMenuRequest.GuestItemId(open) is { Length: > 0 },
            "A guest's item does not carry the guest's id.");
        // Save waits for Open: the guest's command path decides, and a disabled item cannot be chosen.
        Check(Find(bar, "File", "Save") is { IsEnabled: false }, "Save is enabled before its command can run.");
        Check(!native.Choose("File", "Save"), "A disabled item ran.");
        Check(Find(bar, "View", "Grid") is { IsChecked: false }, "Grid is not an unchecked check item.");
        Check(Find(bar, "File", "Recent", "notes.txt") is not null, "The submenu is missing.");

        // Choosing an item, or pressing its shortcut, runs the guest's command (menu-invoke).
        Check(native.Choose("File", "Open…"), "Open… could not be chosen.");
        Check(await TestTimeouts.UntilAsync(() => fixture.GuestErrors.Contains("RAN file.open"), Timeout), "The guest did not run Open.");
        // update-menu-items: Save became available.
        bar = await native.WaitForMenuBarAsync(menus => Find(menus, "File", "Save") is { IsEnabled: true }, Timeout);
        Check(native.Choose("File", "Save"), "Save could not be chosen once available.");
        Check(native.Choose("File", "Recent", "notes.txt"), "The submenu's item could not be chosen.");
        Check(await TestTimeouts.UntilAsync(() => fixture.GuestErrors.Contains("RAN file.save") && fixture.GuestErrors.Contains("RAN file.recent notes.txt"), Timeout),
            "The guest did not run Save and the recent file with its parameter.");
        // The ViewModel's check mark (IRunicMenuCheckedSource) follows its PropertyChanged.
        Check(native.Choose("View", "Grid"), "Grid could not be chosen.");
        await native.WaitForMenuBarAsync(menus => Find(menus, "View", "Grid") is { IsChecked: true }, Timeout);

        // The application declares its bar in German: the window shows the new titles with the current states.
        Check(native.Choose("View", "Deutsch"), "Deutsch could not be chosen.");
        bar = await native.WaitForMenuBarAsync(menus => HasItem(menus, "Datei", "Öffnen…"), Timeout);
        Check(Find(bar, "Datei", "Sichern") is { IsEnabled: true } && Find(bar, "Ansicht", "Raster") is { IsChecked: true },
            $"The German bar lost the states: {HostedWindowHost.Describe(bar)}.");
        Check(!HasItem(bar, "Host", "Host item"), "The host application's bar shows in the guest's window.");

        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{fixture.GuestErrors}");
        Check(!fixture.GuestErrors.Contains("RAN file.save\nRAN file.save"), "Save ran twice.");
    }

    [Test, NotInParallel(Order = 61)]
    public async Task APolicyChangesOrRefusesTheGuestsMenus()
    {
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var hostCommands = new RecordingCommands();
        fixture.Desktop.MenuCommands = hostCommands;
        // The policy hides Save and adds an item of the host's own to the guest's File menu.
        var policy = new MenuPolicy(request => request with
        {
            Entries = [.. request.Entries.Cast<DesktopMenu>().Select(menu => menu.Role != DesktopMenuRole.File ? menu : menu with
            {
                Items = [.. menu.Items.Where(entry => entry is not DesktopMenuItem { Title: "Save" }), new DesktopMenuItem("Close Tool", "host.close-tool")],
            })],
        });
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("menus"), new RunicHostedApplicationOptions { Policy = policy });
        var window = await opened.WaitScaledAsync(Timeout);
        var native = fixture.Factory.Hosts.Single();
        var bar = await native.WaitForMenuBarAsync(menus => HasItem(menus, "File", "Close Tool"), Timeout);
        Check(!HasItem(bar, "File", "Save"), "The policy's change did not apply.");
        Check(policy.Requests.Any(static request => request.Kind == RunicHostedMenuKind.MenuBar && request.Entries.Length == 2), "The policy did not see the guest's bar.");
        // The host's item runs the host application's command, not the guest's.
        Check(native.Choose("File", "Close Tool"), "The host's item could not be chosen.");
        Check(hostCommands.Ran.SequenceEqual(["host.close-tool"]), $"The host ran {string.Join(", ", hostCommands.Ran)}.");
        await Task.Delay(TestTimeouts.Scale(TimeSpan.FromMilliseconds(300)));
        Check(!fixture.GuestErrors.Contains("RAN "), "The guest ran a host item's command.");

        // A refused bar shows no menus of the guest.
        policy.Refuse = true;
        Check(native.Choose("File", "Open…"), "Open… could not be chosen.");
        await native.WaitForMenuBarAsync(static menus => menus.Count == 0, Timeout);
        Check(window.MenuBar.Count == 0, "RunicHostedWindow.MenuBar shows a refused bar.");
        Check(await window.RequestCloseAsync(), "The guest refused the close.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{fixture.GuestErrors}");
    }

    [Test, NotInParallel(Order = 62)]
    public async Task TheHostShowsTheGuestsContextMenusAndDialogsOwnedByItsWindow()
    {
        var folder = Directory.CreateTempSubdirectory("runic-hosted-dialogs-");
        try
        {
            var notes = Path.Combine(folder.FullName, "notes.txt");
            await File.WriteAllTextAsync(notes, "hello");
            var second = Path.Combine(folder.FullName, "second.txt");
            await File.WriteAllTextAsync(second, "two");
            var report = Path.Combine(folder.FullName, "report.txt");
            var dialogs = new FakeHostDialogs
            {
                Answer = async (dialog, cancellationToken) => dialog switch
                {
                    { Kind: RunicHostedDialogKind.OpenFile, File.Title: "Import" } => (IReadOnlyList<string>)[notes],
                    { Kind: RunicHostedDialogKind.OpenFile, File.Title: "Dismiss" } => null,
                    { Kind: RunicHostedDialogKind.OpenFiles } => (IReadOnlyList<string>)[notes, second],
                    { Kind: RunicHostedDialogKind.SaveFile } => (IReadOnlyList<string>)[report],
                    { Kind: RunicHostedDialogKind.OpenFolder } => (IReadOnlyList<string>)[folder.FullName],
                    { Kind: RunicHostedDialogKind.MessageBox, MessageBox.Message: "Save changes?" } => "discard",
                    { Kind: RunicHostedDialogKind.MessageBox, MessageBox.Message: "Hold" } => await Hold(cancellationToken),
                    _ => null,
                },
            };
            using var installed = dialogs.Install();
            await using var fixture = await HostFixture.StartAsync();
            if (await fixture.RefusedAsElevatedAsync()) return;
            ImmutableArray<DesktopMenuEntry> shownMenu = default;
            DesktopContextMenuOptions? shownAt = null;
            fixture.Factory.ContextMenu = (items, options, _) =>
            {
                (shownMenu, shownAt) = (items, options);
                return Task.FromResult(items.OfType<DesktopMenuItem>().FirstOrDefault(static item => item.Title == "Copy"));
            };
            var policy = new DialogPolicy();
            var opened = fixture.Next(opened: true);
            var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("services"), new RunicHostedApplicationOptions { Policy = policy });
            var window = await opened.WaitScaledAsync(Timeout);
            Check(await TestTimeouts.UntilAsync(() => fixture.GuestErrors.Contains("DONE"), Timeout), $"The guest did not finish: {fixture.GuestErrors}");
            var errors = fixture.GuestErrors;
            void Expect(string line) => Check(errors.Contains(line), $"The guest did not report '{line}':\n{errors}");

            // A context menu at the guest's point; the guest receives its own item.
            Expect("CONTEXT edit.copy");
            Check(shownAt is { X: 10, Y: 20 } && shownMenu.Length == 4 && shownMenu[3] is DesktopMenuItem { Title: "Paste", IsEnabled: false } &&
                shownMenu[1] is DesktopMenuItem { Shortcut: { } copyShortcut } && copyShortcut == new DesktopShortcut("C"),
                "The context menu lost its entries, states or position.");
            // File dialogs: the guest opens what the host's dialog selected, with its own leases.
            Expect("OPEN notes.txt hello");
            Expect("OPEN-MANY notes.txt,second.txt");
            Expect("SAVE " + report);
            Expect("FOLDER " + folder.FullName);
            Expect("DISMISSED dismissed");
            Check(dialogs.Shown.Any(static dialog => dialog is { Kind: RunicHostedDialogKind.OpenFile, File: { Title: "Import", Filters: [{ Name: "Text" }] } }),
                "The open dialog lost its title or filter.");
            Check(dialogs.Shown.Any(static dialog => dialog is { Kind: RunicHostedDialogKind.SaveFile, File.SuggestedName: "report.txt" }),
                "The save dialog lost its suggested name.");
            // A message box returns the chosen button.
            Expect("BOX discard");
            Check(dialogs.Shown.Any(static dialog => dialog.MessageBox is { Kind: DesktopMessageBoxKind.Warning, Detail: "Your changes are lost otherwise.", Buttons.Length: 3 } box &&
                box.Buttons[1] is { Id: "discard", Role: DesktopMessageBoxButtonRole.Destructive }), "The message box lost its kind, detail or buttons.");
            // One dialog at a time, and the guest's cancellation closes the host's dialog (cancel).
            Expect("BUSY failed ResourceBusy");
            Expect("HOLD cancelled");
            Check(await TestTimeouts.UntilAsync(() => dialogs.Cancelled.Any(static dialog => dialog.MessageBox?.Message == "Hold"), Timeout),
                "The host's dialog stayed open after the guest gave it up.");
            // The policy refuses: the guest learns PermissionDenied, and the host showed nothing.
            Expect("REFUSED failed PermissionDenied");
            Check(!dialogs.Shown.Any(static dialog => dialog.MessageBox?.Message == "Refuse"), "The host showed a refused dialog.");
            Check(policy.Kinds.Contains(RunicHostedDialogKind.OpenFolder) && policy.Kinds.Contains(RunicHostedDialogKind.MessageBox),
                "The policy did not see the dialogs.");

            Check(await window.RequestCloseAsync(), "The guest refused the close.");
            Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{fixture.GuestErrors}");
        }
        finally
        {
            folder.Delete(recursive: true);
        }

        static async Task<object?> Hold(CancellationToken cancellationToken)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return null;
        }
    }

    [Test, NotInParallel(Order = 63)]
    public async Task ClosingTheWindowClosesItsDialog()
    {
        var dialogs = new FakeHostDialogs
        {
            Answer = static async (_, cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                return null;
            },
        };
        using var installed = dialogs.Install();
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("dialog-closed"));
        var window = await opened.WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => dialogs.Shown.Count == 1, Timeout), "The host did not show the guest's message box.");
        // The user closes the window while the box is shown: the host closes the box, and the guest's box reports the
        // closed owner.
        fixture.Factory.Hosts.Single().UserClose();
        await window.Closed.WaitScaledAsync(Timeout);
        Check(await TestTimeouts.UntilAsync(() => dialogs.Cancelled.Count == 1, Timeout), "The host's box stayed open after its window closed.");
        Check(await application.Exited.WaitScaledAsync(Timeout) == 0, $"The guest did not exit cleanly:\n{fixture.GuestErrors}");
        Check(fixture.GuestErrors.Contains("BOX unavailable OwnerClosed"), $"The guest's box did not report the closed window:\n{fixture.GuestErrors}");
    }

    [Test, NotInParallel(Order = 64)]
    public async Task AGuestThatCrashesDuringADialogLeavesNoDialogOpen()
    {
        var dialogs = new FakeHostDialogs
        {
            Answer = static async (_, cancellationToken) =>
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                return null;
            },
        };
        using var installed = dialogs.Install();
        await using var fixture = await HostFixture.StartAsync();
        if (await fixture.RefusedAsElevatedAsync()) return;
        var opened = fixture.Next(opened: true);
        var application = await fixture.Applications.StartAsync(HostedGuestProgram.Guest("dialog-crash"));
        var window = await opened.WaitScaledAsync(Timeout);
        Check(await application.Exited.WaitScaledAsync(Timeout) == HostedGuestProgram.CrashExitCode, "The guest did not crash.");
        Check(dialogs.Shown.Count == 1 && dialogs.Cancelled.Count == 1, "The host's dialog stayed open after the guest crashed.");
        Check(window.Closed.IsCompleted, "The guest's window is still open.");
    }

    // The shapes of menus/1 and dialogs/1 (HostFeatures), which the guest, the host and RunicTestHost share.
    [Test]
    public async Task MenuAndDialogShapesAreChecked()
    {
        static JsonArray Bar(int depth, int width = 1)
        {
            JsonArray items = [new JsonObject { ["kind"] = "item", ["id"] = "leaf", ["title"] = "Leaf" }];
            for (var level = 1; level < depth; level++)
                items = [new JsonObject { ["kind"] = "submenu", ["title"] = "Level", ["items"] = items }];
            for (var index = 1; index < width; index++)
                items.Add(new JsonObject { ["kind"] = "separator" });
            return [new JsonObject { ["kind"] = "submenu", ["title"] = "Top", ["items"] = items }];
        }
        Check(HostFeatures.CheckMenu(Bar(7), bar: true) is null, "A bar of 8 levels is refused.");
        Check(HostFeatures.CheckMenu(Bar(8), bar: true) == "capacity", "A bar of 9 levels is not over capacity.");
        Check(HostFeatures.CheckMenu(Bar(1, width: 999), bar: true) is null, "A bar of 1,000 entries is refused.");
        Check(HostFeatures.CheckMenu(Bar(1, width: 1_000), bar: true) == "capacity", "A bar of 1,001 entries is not over capacity.");
        Check(HostFeatures.CheckMenu(new JsonArray(new JsonObject { ["kind"] = "item", ["id"] = "a", ["title"] = "A" }), bar: true) == "invalid-request",
            "A bar with an item at its top level is accepted.");
        Check(HostFeatures.CheckMenu(new JsonArray(new JsonObject { ["kind"] = "item", ["id"] = "a", ["title"] = "A" }), bar: false) is null,
            "A context menu item is refused.");
        Check(HostFeatures.CheckMenu(new JsonArray(), bar: false) == "invalid-request", "An empty context menu is accepted.");
        var duplicate = new JsonArray(new JsonObject { ["kind"] = "item", ["id"] = "a", ["title"] = "A" }, new JsonObject { ["kind"] = "item", ["id"] = "a", ["title"] = "B" });
        Check(HostFeatures.CheckMenu(duplicate, bar: false) == "invalid-request", "Two items with one id are accepted.");
        foreach (var item in (JsonObject[])
        [
            new() { ["kind"] = "item", ["id"] = "a", ["title"] = " " },
            new() { ["kind"] = "item", ["id"] = "a", ["title"] = new string('x', 129) },
            new() { ["kind"] = "item", ["id"] = "a", ["title"] = "A", ["shortcut"] = "Primary+Nope" },
            new() { ["kind"] = "item", ["id"] = "a", ["title"] = "A", ["radioGroup"] = "g" },
            new() { ["kind"] = "item", ["id"] = "a", ["title"] = "A", ["accessKey"] = "ab" },
            new() { ["kind"] = "item", ["id"] = "", ["title"] = "A" },
            new() { ["kind"] = "frame", ["id"] = "a", ["title"] = "A" },
        ])
            Check(HostFeatures.CheckMenu(new JsonArray(item), bar: false) == "invalid-request", $"{item.ToJsonString()} is accepted.");
        Check(HostFeatures.CheckFileDialog(new JsonObject { ["dialog"] = "save", ["suggestedName"] = "a.txt" }), "A save dialog is refused.");
        Check(!HostFeatures.CheckFileDialog(new JsonObject { ["dialog"] = "save" }), "A save dialog without a name is accepted.");
        Check(!HostFeatures.CheckFileDialog(new JsonObject { ["dialog"] = "open", ["suggestedName"] = "a.txt" }), "An open dialog with a name is accepted.");
        Check(!HostFeatures.CheckFileDialog(new JsonObject { ["dialog"] = "folder", ["filters"] = new JsonArray() }), "A folder dialog with filters is accepted.");
        Check(!HostFeatures.CheckFileDialog(new JsonObject { ["dialog"] = "print" }), "An unknown dialog is accepted.");
        Check(HostFeatures.CheckMessageBox(new JsonObject { ["message"] = "Hi", ["icon"] = "sparkles" }), "An unknown icon is refused.");
        Check(!HostFeatures.CheckMessageBox(new JsonObject { ["message"] = " " }), "A blank message is accepted.");
        var buttons = new JsonArray([.. Enumerable.Range(0, 7).Select(static index => (JsonNode)new JsonObject { ["id"] = $"b{index}", ["label"] = "B" })]);
        Check(!HostFeatures.CheckMessageBox(new JsonObject { ["message"] = "Hi", ["buttons"] = buttons }), "Seven buttons are accepted.");
        await Task.CompletedTask;
    }

    private sealed class RecordingCommands : IDesktopMenuCommandTarget
    {
        public List<string> Ran { get; } = [];
        public bool CanExecute(DesktopMenuItem item) => item.CommandId.StartsWith("host.", StringComparison.Ordinal);
        public void Execute(DesktopMenuItem item)
        {
            lock (Ran) Ran.Add(item.CommandId);
        }
    }

    private sealed class MenuPolicy(Func<RunicHostedMenuRequest, RunicHostedMenuRequest> change) : IRunicHostedWindowPolicy
    {
        private readonly List<RunicHostedMenuRequest> _requests = [];
        public volatile bool Refuse;

        public IReadOnlyList<RunicHostedMenuRequest> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        public RunicHostedMenuDecision Menu(RunicHostedMenuRequest request)
        {
            lock (_requests) _requests.Add(request);
            return Refuse ? RunicHostedMenuDecision.Refuse("No menus.") : RunicHostedMenuDecision.Apply(change(request));
        }
    }

    private sealed class DialogPolicy : IRunicHostedWindowPolicy
    {
        private readonly List<RunicHostedDialogKind> _kinds = [];

        public IReadOnlyList<RunicHostedDialogKind> Kinds
        {
            get { lock (_kinds) return [.. _kinds]; }
        }

        public ValueTask<RunicHostedDialogDecision> DialogAsync(RunicHostedDialogRequest request, CancellationToken cancellationToken)
        {
            lock (_kinds) _kinds.Add(request.Kind);
            return ValueTask.FromResult(request.MessageBox?.Message == "Refuse"
                ? RunicHostedDialogDecision.Refuse("Not this one.")
                : RunicHostedDialogDecision.Allow());
        }
    }
}
