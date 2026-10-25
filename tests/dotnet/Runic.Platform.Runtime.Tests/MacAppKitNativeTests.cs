using System.Threading.Channels;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using TUnit.Core;
using static Runic.Platform.MacOS.MacDesktopNative;

// The macOS services that need AppKit without a user: a real NSStatusItem in the menu bar, its menu and actions, the
// login item of an unbundled process, bringing a Desktop window forward, and Carbon global shortcuts. Program.cs runs
// the category inside the Desktop event loop, so the status item starts without a window, as in a windowless
// application. CI runs it on the macOS runner under JIT and NativeAOT. Opening the menu and SMAppService with a signed
// bundle stay manual checks.
internal sealed partial class MacAppKitNativeTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativeAppKit"), DisplayName("native AppKit: status item, login item, window presentation, message boxes and the Dock tile"), Timeout(180_000)]
    public async Task NativeAppKitAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The AppKit checks need macOS.");
        await MainThreadReadyAsync();
        await StatusItemAsync();
        await StatusItemQueueAsync();
        await LoginItemAsync();
        await PresentationAsync(cancellationToken);
        await FilterChoiceAsync();
        await MessageBoxesAsync(cancellationToken);
        await GlobalShortcutsAsync();
        await DockTileAsync(cancellationToken);
    }

    // The Dock tile's badge label and progress bar, read back from AppKit; what the Dock draws stays a manual check.
    private static async Task DockTileAsync(CancellationToken cancellationToken)
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false }, cancellationToken);
        await using var surface = await host.CreateSurfaceAsync(new()
        {
            Content = new DesktopContent.Html("<!doctype html><meta charset=\"utf-8\"><title>Runic dock</title><p>Dock</p>"),
        }, cancellationToken);
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, Width = 320, Height = 240 }, cancellationToken);
        var owner = new WindowOwner(window);
        var tile = new MacDockTile(owner);
        foreach (var (badge, label) in new (DesktopBadge?, string?)[] { (new() { Count = 5 }, "5"), (new() { Count = 120 }, "99+"), (new(), "\u2022"), (null, null) })
        {
            var result = await tile.SetBadgeAsync(badge, cancellationToken);
            string? shown = null;
            await MacOsMainQueue.InvokeAsync(() => shown = MacDockTile.BadgeLabel());
            Require(result is PlatformResult<PlatformUnit>.Success && shown == label, $"The badge {badge}: {result}, label {shown ?? "none"}.");
        }
        Pass("the Dock tile's badge label shows a count, 99+ and a dot, and clears");

        foreach (var (progress, expected) in new (DesktopProgress?, (bool, bool, double))[]
            { (new(0.4), (true, false, 0.4)), (new(null), (true, true, 0.4)), (new(1) { State = DesktopProgressState.Error }, (true, false, 1)), (null, (false, false, 0)) })
        {
            var result = await tile.SetProgressAsync(progress, cancellationToken);
            (bool, bool, double) shown = default;
            await MacOsMainQueue.InvokeAsync(() => shown = MacDockTile.Progress());
            Require(result is PlatformResult<PlatformUnit>.Success && shown == expected, $"The progress {progress}: {result}, shown {shown}.");
        }
        Pass("the Dock tile's progress bar shows a value and indeterminate progress, and clears");

        // The most recent window wins; a window's badge goes when its presentation stops.
        var other = new MacDockTile(owner);
        Require(await tile.SetBadgeAsync(new() { Count = 1 }, cancellationToken) is PlatformResult<PlatformUnit>.Success
            && await other.SetBadgeAsync(new() { Count = 2 }, cancellationToken) is PlatformResult<PlatformUnit>.Success, "Two windows' badges.");
        await tile.DisposeAsync();
        string? kept = null;
        await MacOsMainQueue.InvokeAsync(() => kept = MacDockTile.BadgeLabel());
        Require(kept == "2", $"Stopping a window that no longer owns the badge keeps the other's: {kept}.");
        await other.DisposeAsync();
        await MacOsMainQueue.InvokeAsync(() => kept = MacDockTile.BadgeLabel());
        Require(kept is null, $"Stopping the badge's window clears it: {kept}.");
        await window.CloseAsync(cancellationToken);
        Require(await new MacDockTile(owner).SetBadgeAsync(new() { Count = 3 }, cancellationToken) is PlatformResult<PlatformUnit>.Unavailable, "A closed window's badge.");
        Pass("the most recent window's badge wins and goes with its window");
    }

    // NSAlert sheets on a Desktop window, answered in process with performClick: (the runner grants no Accessibility
    // permission for synthesized keys); the key equivalents show which buttons Return and Esc choose.
    private static async Task MessageBoxesAsync(CancellationToken cancellationToken)
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false }, cancellationToken);
        await using var surface = await host.CreateSurfaceAsync(new()
        {
            Content = new DesktopContent.Html("<!doctype html><meta charset=\"utf-8\"><title>Runic message boxes</title><p>Message boxes</p>"),
        }, cancellationToken);
        await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, Width = 480, Height = 320 }, cancellationToken);
        var owner = new WindowOwner(window);
        var save = new DesktopMessageBoxButton("save", "Save") { Role = DesktopMessageBoxButtonRole.Default };
        var discard = new DesktopMessageBoxButton("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive };
        var cancel = new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };
        var close = new DesktopMessageBox("Save changes?") { Detail = "Your changes are lost otherwise.", Kind = DesktopMessageBoxKind.Warning, Buttons = [discard, cancel, save] };

        string layout = "";
        var result = await ShowAsync(owner, close, alert =>
        {
            layout = string.Join(";", Buttons(alert).Select(static button =>
                $"{Text(Send(button, Sel("title")))}[{Text(Send(button, Sel("keyEquivalent"))).Replace("\r", "Return", StringComparison.Ordinal).Replace("\u001b", "Esc", StringComparison.Ordinal)}]" +
                (BoolArg(button, Sel("respondsToSelector:"), Sel("hasDestructiveAction")) != 0 && Bool(button, Sel("hasDestructiveAction")) != 0 ? "!" : "")));
            return Buttons(alert)[0];
        }, cancellationToken);
        Require(layout == "Save[Return];Cancel[Esc];Don't Save[]!", $"Unexpected sheet buttons {layout}.");
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(var saved) && saved == save, $"The default button's click: {result}.");
        result = await ShowAsync(owner, close, alert => Buttons(alert)[1], cancellationToken);
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(var cancelled) && cancelled == cancel, $"The cancel button's click: {result}.");
        Pass($"the sheet orders the buttons, keys Return and Esc, marks the destructive one and returns the clicked button: {layout}");

        result = await ShowAsync(owner, new DesktopMessageBox("Exported."), alert => Buttons(alert) is [var only] ? only : 0, cancellationToken);
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"A box without buttons: {result}.");
        Pass("a box without buttons shows NSAlert's OK and answers null");

        var boxes = new MacMessageBoxes(owner);
        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Shown = alert => shown.TrySetResult(alert);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = boxes.ShowAsync(close, cancellation.Token).AsTask();
            var alert = await shown.Task.WaitAsync(Step, cancellationToken);
            cancellation.Cancel();
            try { await pending.WaitAsync(Step, cancellationToken); throw new InvalidOperationException("A cancelled sheet returned."); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await WaitAsync(() => WindowAsync(owner, static handle => Send(handle, Sel("attachedSheet")) == 0), "the cancelled sheet ends");
        }
        Pass("cancellation ends the sheet");
        await window.CloseAsync(cancellationToken);
        Require(await new MacMessageBoxes(owner).ShowAsync(close, cancellationToken) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "A closed window's box.");
        Pass("a closed window reports its boxes closed");
    }

    // Shows a sheet and clicks the button that choose picks, on the main thread once the sheet began.
    private static async Task<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(INativeWindowOwner owner, DesktopMessageBox box, Func<nint, nint> choose,
        CancellationToken cancellationToken)
    {
        var boxes = new MacMessageBoxes(owner);
        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Shown = alert => shown.TrySetResult(alert);
        var pending = boxes.ShowAsync(box, cancellationToken).AsTask();
        var alert = await shown.Task.WaitAsync(Step, cancellationToken);
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            Arg(choose(alert), Sel("performClick:"), 0);
        });
        return await pending.WaitAsync(Step, cancellationToken);
    }

    private static nint[] Buttons(nint alert)
    {
        var buttons = Send(alert, Sel("buttons"));
        return [.. Enumerable.Range(0, (int)Send(buttons, Sel("count"))).Select(index => Arg(buttons, Sel("objectAtIndex:"), index))];
    }

    // The file panel's filter pop-up (ShowFilterChoice): the registered target class swaps allowedContentTypes. The
    // panel is never shown; choosing in a shown panel is a manual check.
    private static async Task FilterChoiceAsync()
    {
        var observed = await MacOsFilePicker.FilterChoice.InspectAsync(
        [
            new FileDialogFilter("Images", ["png", "jpg"]) { MimeTypes = ["image/gif"] },
            new FileDialogFilter("Text", ["txt"]),
            new FileDialogFilter("Text", ["md"]) { MimeTypes = ["text/*"] },
            new FileDialogFilter("All files", ["*"]),
        ]).AsTask().WaitAsync(Step);
        Require(observed is [(3, "Images"), (1, "Text"), (2, "Text"), (0, "All files")],
            "Unexpected filter pop-up: " + string.Join(", ", observed.Select(static item => $"{item.Count} {item.Selected}")));
        Pass("the filter pop-up keeps filters with the same name and swaps the panel's types for the chosen filter");
    }

    // The event loop's [NSApp run] launches the application while the tests already run; the status item's requests give
    // up after MacStatusIcon.MainThreadTimeout. Waits until the main thread serves its queue, and reports how long the
    // launch kept it.
    private static async Task MainThreadReadyAsync()
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        bool running = false, finished = false;
        long policy = -1;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            running = Bool(application, Sel("isRunning")) != 0;
            finished = Bool(Send(Class("NSRunningApplication"), Sel("currentApplication")), Sel("isFinishedLaunching")) != 0;
            policy = Send(application, Sel("activationPolicy"));
        }).AsTask().WaitAsync(TimeSpan.FromSeconds(90));
        Console.WriteLine($"main thread: first queued work ran after {waited.ElapsedMilliseconds} ms; isRunning={running}, " +
            $"isFinishedLaunching={finished}, activationPolicy={policy}");
    }

    private static async Task StatusItemAsync()
    {
        var activations = Channel.CreateUnbounded<DesktopStatusIconActivation>();
        await using var icon = new MacStatusIcon();
        icon.Activated += (_, activation) => activations.Writer.TryWrite(activation);
        var glyph = new DesktopIcon { Images = [Glyph(16, 0), Glyph(32, 0)] };
        var badge = new DesktopIcon { Images = [Glyph(16, 0xc8)] };
        var state = new DesktopStatusIconState(glyph, "Runic status item")
        {
            Tooltip = "All quiet",
            AttentionIcon = badge,
            Menu =
            [
                new DesktopMenuItem("Open window", "open"),
                new DesktopMenuItem("Check daily", "daily") { IsChecked = true },
                new DesktopMenuSeparator(),
                new DesktopMenu("More",
                [
                    new DesktopMenuItem("Ask for attention", "attention"),
                    new DesktopMenuItem("Unavailable", "disabled") { IsEnabled = false },
                ]),
                new DesktopMenuItem("Quit", "quit"),
            ],
        };
        var started = System.Diagnostics.Stopwatch.StartNew();
        var showing = await icon.ShowAsync(state).AsTask().WaitAsync(Step);
        Console.WriteLine($"status item: the first ShowAsync returned {showing} after {started.ElapsedMilliseconds} ms");
        Require(showing is PlatformResult<PlatformUnit>.Success,
            $"The status item was not shown: {showing} after {started.ElapsedMilliseconds} ms (MainThreadTimeout {MacStatusIcon.MainThreadTimeout}).");
        var shown = await icon.InspectAsync();
        Pass($"status item shown: {shown}");
        Require(shown is { Shown: true, HasImage: true, Template: true, Representations: 2, Title: "", ToolTip: "Runic status item\nAll quiet", AccessibilityLabel: "Runic status item" },
            $"Unexpected status item {shown}.");
        Require(await ApplicationAsync(static application => Send(application, Sel("activationPolicy"))) == 1,
            "A windowless unbundled process should run as an accessory application.");

        await icon.PressAsync(accessibility: false);
        await ExpectAsync(activations, DesktopStatusIconActivationKind.Primary, null);
        // VoiceOver's press: the current event is then no mouse release in the button's window.
        await icon.PressAsync(accessibility: true);
        await ExpectAsync(activations, DesktopStatusIconActivationKind.Primary, null);
        Pass("pressing the button, also through accessibility, raises the primary activation");

        var opened = await icon.ShowMenuAsync(() => { icon.CloseMenu(); return ValueTask.CompletedTask; });
        Require(opened is { Handled: true, Opened: true, MenuKept: false }, $"VoiceOver's show-menu action did not open the menu once: {opened}.");
        Require(await icon.InspectAsync() is { Shown: true }, "Closing the menu removed the item.");
        await icon.PressAsync(accessibility: false);
        await ExpectAsync(activations, DesktopStatusIconActivationKind.Primary, null);
        Pass("VoiceOver's show-menu action opens the menu, which the item gives back when it closes, so a press is primary again");

        PlatformResult<PlatformUnit>? hidden = null;
        var whileOpen = await icon.ShowMenuAsync(async () => hidden = await icon.HideAsync().AsTask().WaitAsync(Step));
        Require(whileOpen.Opened && hidden is PlatformResult<PlatformUnit>.Success, $"Hiding with the menu open: {whileOpen}, {hidden}.");
        Require(await icon.InspectAsync() is { Shown: false }, "Hiding with the menu open left the item.");
        Require(await icon.ShowAsync(state).AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "The item did not come back after the menu.");
        Pass("hiding while the menu is open closes it first, then removes the item");

        var menu = await icon.ChooseMenuItemAsync(0);
        Require(menu == "Open window;Check daily[on];-;More(Ask for attention;Unavailable[disabled]);Quit", $"Unexpected menu {menu}.");
        await ExpectAsync(activations, DesktopStatusIconActivationKind.MenuItem, "open");
        await icon.ChooseMenuItemAsync(3, 0);
        await ExpectAsync(activations, DesktopStatusIconActivationKind.MenuItem, "attention");
        Pass($"the menu is built from the state and its items route to their IDs: {menu}");

        Require(await icon.ShowAsync(state with { Status = DesktopStatusIconStatus.NeedsAttention, Tooltip = null }).AsTask().WaitAsync(Step)
            is PlatformResult<PlatformUnit>.Success, "The attention state was not shown.");
        var attention = await icon.InspectAsync();
        Require(attention is { HasImage: true, Template: false, Representations: 1, ToolTip: "Runic status item" }, $"Unexpected attention item {attention}.");
        Pass($"the attention state shows the colour attention image: {attention}");

        Require(await icon.ShowAsync(new DesktopStatusIconState(new DesktopIcon { IconName = "applications-system" }, "Runic"))
            .AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "The text item was not shown.");
        var text = await icon.InspectAsync();
        Require(text is { HasImage: false, Title: "Runic" }, $"Unexpected text item {text}.");
        Pass("an icon without images shows the title");

        Require(await icon.HideAsync().AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "Hiding failed.");
        Require(await icon.InspectAsync() is { Shown: false }, "The status item is still in the menu bar.");
        Require(await icon.ShowAsync(state).AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success
            && (await icon.InspectAsync()).Shown, "The status item did not come back.");
        Pass("hiding removes the item and showing adds it again");
    }

    // Requests reach the main thread in order: a show the caller gave up on never runs after a later hide, and without
    // a serviced main queue a request reports unavailable instead of waiting, and disposal ends.
    private static async Task StatusItemQueueAsync()
    {
        var state = new DesktopStatusIconState(new DesktopIcon { Images = [Glyph(16, 0)] }, "Runic queue");
        await using (var icon = new MacStatusIcon())
        {
            Require(await icon.ShowAsync(state).AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "Show.");
            Require(await icon.HideAsync().AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "Hide.");
            var busy = BusyMainThreadAsync(TimeSpan.FromSeconds(2));
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                try
                {
                    await icon.ShowAsync(state, cancel.Token);
                    throw new InvalidOperationException("A show cancelled while the main thread was busy completed.");
                }
                catch (OperationCanceledException) { }
            }
            Require(await icon.HideAsync().AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "Hide after a cancelled show.");
            await busy;
            Require(await icon.InspectAsync() is { Shown: false }, "A cancelled show appeared after a later hide.");
            Pass("a cancelled show does not appear after a later hide");
        }

        var timeout = MacStatusIcon.MainThreadTimeout;
        MacStatusIcon.MainThreadTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var icon = new MacStatusIcon();
            var busy = BusyMainThreadAsync(TimeSpan.FromSeconds(2));
            var result = await icon.ShowAsync(state).AsTask().WaitAsync(Step);
            Require(result is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable), $"Show without a serviced main queue: {result}.");
            await icon.DisposeAsync().AsTask().WaitAsync(Step);
            await busy;
            Require(await icon.InspectAsync() is { Shown: false }, "The abandoned show ran later.");
            Pass("without a serviced main queue a show reports unavailable, never runs later, and disposal ends");
        }
        finally { MacStatusIcon.MainThreadTimeout = timeout; }
    }

    private static Task BusyMainThreadAsync(TimeSpan duration)
    {
        var busy = MacOsMainQueue.InvokeAsync(() => Thread.Sleep(duration)).AsTask();
        return Task.WhenAll(busy, Task.Delay(100));
    }

    private static async Task LoginItemAsync()
    {
        Require(MacLoginStartup.IsServiceAvailable, "SMAppService should exist on the macOS 13+ runner.");
        Require(!MacLoginStartup.IsBundled, "The test host is not an application bundle.");
        var startup = new MacLoginStartup();
        var unavailable = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        Require(await startup.IsEnabledAsync() is PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "IsEnabledAsync.");
        Require(Equals(await startup.EnableAsync(new LoginStartupRequest([])), unavailable), "EnableAsync.");
        Require(Equals(await startup.DisableAsync(), unavailable), "DisableAsync.");
        Pass("an unbundled process reports login startup unavailable");
    }

    private static async Task PresentationAsync(CancellationToken cancellationToken)
    {
        // AppKit gave the process a default main menu; without one the host builds the whole menu itself.
        await MacOsMainQueue.InvokeAsync(static () => Arg(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("setMainMenu:"), 0));
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false }, cancellationToken);
        await using var surface = await host.CreateSurfaceAsync(new()
        {
            Content = new DesktopContent.Html("<!doctype html><meta charset=\"utf-8\"><title>Runic presentation</title><p>Presentation</p>"),
        }, cancellationToken);
        await using var window = await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded,
            PresentationPolicy = DesktopPresentationPolicy.RequestedOnly,
            Hidden = true,
            Width = 480,
            Height = 320,
        }, cancellationToken);
        var owner = new WindowOwner(window);
        Require(!await WindowAsync(owner, static handle => Bool(handle, Sel("isVisible")) != 0), "The hidden window is visible.");
        Require(await ApplicationAsync(static application => Send(application, Sel("activationPolicy"))) == 0,
            "An unbundled process with a window should run as a regular application.");
        var menu = await MenuAsync();
        foreach (var expected in new[] { "About ", "Hide Others;Show All;;Quit ", "Edit(Undo;Redo;;Cut;Copy;Paste;Select All", "View(Actual Size;Zoom In",
            "Window(Minimize;Zoom;;Bring All to Front" })
            Require(menu.Contains(expected, StringComparison.Ordinal), $"The window host's main menu lacks {expected}: {menu}.");
        Pass($"the main menu has the zoom items: {menu}");
        var presented = await MacWindowPresenter.PresentAsync(owner, cancellationToken);
        await WaitAsync(() => WindowAsync(owner, static handle => Bool(handle, Sel("isVisible")) != 0), "the presented window is visible");
        var key = await WindowAsync(owner, static handle => Bool(handle, Sel("isKeyWindow")) != 0);
        var active = await ApplicationAsync(static application => Bool(application, Sel("isActive"))) != 0;
        // The runner's GUI session lets the process activate.
        Require(key || active, $"The presented window is neither key nor in an active application: {presented}.");
        Pass($"presenting orders a hidden window front: {presented} (key window: {key}, application active: {active})");
        await window.CloseAsync(cancellationToken);
        try
        {
            _ = await MacWindowPresenter.PresentAsync(owner, cancellationToken);
            throw new InvalidOperationException("Presenting a closed window succeeded.");
        }
        catch (OwnerClosedException) { Pass("presenting a closed window reports it closed"); }
    }

    private static async Task ExpectAsync(Channel<DesktopStatusIconActivation> activations, DesktopStatusIconActivationKind kind, string? id)
    {
        var activation = await activations.Reader.ReadAsync().AsTask().WaitAsync(Step);
        Require(activation.Kind == kind && activation.CommandId == id && activation.PlatformContext is null, $"Expected {kind} {id}; got {activation}.");
    }

    private static async Task WaitAsync(Func<Task<bool>> check, string description)
    {
        var deadline = DateTime.UtcNow + Step;
        while (!await check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {description}.");
            await Task.Delay(100);
        }
    }

    private static async Task<long> ApplicationAsync(Func<nint, long> read)
    {
        long value = 0;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            value = read(Send(Class("NSApplication"), Sel("sharedApplication")));
        });
        return value;
    }

    // The main menu's titles, with each submenu's titles in parentheses.
    private static async Task<string> MenuAsync()
    {
        string text = "";
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            text = Describe(Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("mainMenu")), 0);
        });
        return text;

        static string Describe(nint menu, int depth)
        {
            if (menu == 0) return "(none)";
            var titles = new List<string>();
            for (nint i = 0, count = Send(menu, Sel("numberOfItems")); i < count; i++)
            {
                var item = Arg(menu, Sel("itemAtIndex:"), i);
                var title = Text(Send(item, Sel("title")));
                if (Send(item, Sel("submenu")) is var submenu and not 0 && depth < 1) title += "(" + Describe(submenu, depth + 1) + ")";
                titles.Add(title);
            }
            return string.Join(";", titles);
        }
    }

    private static async Task<bool> WindowAsync(INativeWindowOwner owner, Func<nint, bool> read)
    {
        bool value = false;
        await owner.InvokeAsync(handle => { using var pool = new Pool(); value = read(handle); });
        return value;
    }

    // A filled circle: monochrome with antialiased edges for blue 0, a red badge otherwise.
    private static DesktopIconImage Glyph(int size, byte red)
    {
        var pixels = new byte[size * size * 4];
        double radius = size / 2.0 - 1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x + 0.5 - size / 2.0, 2) + Math.Pow(y + 0.5 - size / 2.0, 2));
                int i = (y * size + x) * 4;
                pixels[i] = red;
                pixels[i + 3] = (byte)Math.Clamp((radius - distance + 0.5) * 255, 0, 255);
            }
        return new DesktopIconImage(size, size, [.. pixels]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private sealed class WindowOwner(DesktopWindow window) : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => window.IsOpen && window.NativeHandle != 0;
        public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable) throw new OwnerClosedException();
            await window.InvokeNativeAsync(action, cancellationToken);
        }
    }
}
