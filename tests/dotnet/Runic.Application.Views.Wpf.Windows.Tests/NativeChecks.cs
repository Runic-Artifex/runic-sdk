using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Navigation.Wpf;
using Runic.Platform;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TUnit.Core;

// The native WPF checks, after the portable cases (WpfDispatcherExecutor runs them on the dispatcher).
internal sealed class NativeChecks
{
    // #267: a WPF window is brought forward for a forwarded launch: shown when hidden or not shown yet, restored when
    // minimized, and reported closed once it closed. A CI desktop may keep another window in the foreground, which
    // flashes the taskbar button instead (PermissionDenied).
    [Test, NotInParallel("WpfSuite", Order = 15)]
    public async Task PresentShowsRestoresAndReportsAClosedWindow()
    {
        static bool Presented(PlatformResult<PlatformUnit> result) =>
            result is PlatformResult<PlatformUnit>.Success or PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied };

        var window = new Window { Width = 320, Height = 200, Title = "Runic WPF present" };
        try
        {
            var shown = await WpfPlatformServices.PresentAsync(window);
            Program.Check(Presented(shown) && window.IsVisible, $"Presenting a window not shown yet did not show it: {shown}.");
            window.WindowState = WindowState.Maximized;
            window.WindowState = WindowState.Minimized;
            var restored = await WpfPlatformServices.PresentAsync(window);
            Program.Check(Presented(restored) && window.WindowState == WindowState.Maximized,
                $"Presenting a minimized window did not restore it to its previous state: {restored}, {window.WindowState}.");
            window.Hide();
            var unhidden = await WpfPlatformServices.PresentAsync(window);
            Program.Check(Presented(unhidden) && window.IsVisible && window.Visibility == Visibility.Visible,
                $"Presenting a hidden window did not show it: {unhidden}, {window.Visibility}.");
            // From another thread, as a forwarded launch arrives.
            var fromPool = await Task.Run(async () => await WpfPlatformServices.PresentAsync(window));
            Program.Check(Presented(fromPool), $"Presenting from a thread-pool thread failed: {fromPool}.");
        }
        finally { window.Close(); }
        var closed = await WpfPlatformServices.PresentAsync(window);
        Program.Check(closed is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            $"Presenting a closed window did not report the owner closed: {closed}.");
    }

    // location.reload() reconnects the page within the same presentation (#196).
    [Test, NotInParallel("WpfSuite", Order = 11)]
    public async Task ChildWebViewAuthenticatesReloadsAndUnloads()
    {
        using var child = new RunicWebView();
        var window = new Window { Content = child, Width = 600, Height = 400, Title = "Runic WPF child smoke" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var attachment = new Attachment();
            var collection = new ServiceCollection();
            collection.AddSingleton<Func<IBridgeTransport, object, IDisposable>>((_, _) => attachment);
            await using var services = collection.BuildServiceProvider();
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
            });
            await using var view = await services.CreateWpfViewAsync(host, Program.HtmlOptions(), new object());
            var connectionIds = new HashSet<ulong>();
            List<string> events = [];
            // OpenAsync completes when the token check succeeds; the session raises its Connected event after that,
            // from a thread-pool thread, so the first event can arrive after OpenAsync has returned.
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var observe = view.Surface.SubscribeConnectionEvents(invocation =>
            {
                lock (connectionIds)
                {
                    events.Add($"{invocation.Kind} {invocation.SessionId}");
                    if (invocation.Kind != DesktopConnectionEventKind.Connected) return;
                    connectionIds.Add(invocation.SessionId);
                    connected.TrySetResult();
                    if (connectionIds.Count > 1) reconnected.TrySetResult();
                }
            });
            await view.OpenAsync();
            await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Program.Check(window.IsVisible && child.Handle != 0, "The WebView did not remain inside the WPF shell.");
            lock (connectionIds)
                Program.Check(connectionIds.Count == 1, $"The first document did not authenticate. Events: {string.Join(", ", events)}.");
            await view.Surface.RunJavaScriptAsync("location.reload();");
            await Task.WhenAny(reconnected.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            lock (connectionIds)
                Program.Check(reconnected.Task.IsCompleted, $"The reloaded document did not reconnect. Events: {string.Join(", ", events)}.");
            Program.Check(!attachment.Disposed, "Browser reload retired the child session.");
            child.Width = 350;
            child.Height = 220;
            window.UpdateLayout();
            Program.Check(((System.Windows.Interop.IKeyboardInputSink)child).TabInto(
                new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next)),
                "WPF could not tab into the child presentation.");
            // TabInto only requests the move: WebView2's MoveFocus has no completion, and the browser process
            // focuses the first element a few milliseconds later, unordered with scripts on the bridge socket.
            // The page waits for the focus to land instead of sampling activeElement once (#216).
            var focus = await view.Surface.ExecuteJavaScriptAsync(FocusProbe("editor"), TimeSpan.FromSeconds(15));
            Program.Check(focus.StartsWith("editor ", StringComparison.Ordinal), $"The child document could not focus its editor: {focus}.");
            window.Content = null;
            await attachment.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await view.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Program.Check(attachment.Disposed && window.IsVisible, "Unloading the web child did not retire its session while preserving the shell.");
            Console.WriteLine("Runic.Application.Views.Wpf native authentication/reload/unload checks passed.");
        }
        finally { window.Close(); }
    }

    // vwpf-01: Tab enters the document at its first or last tab stop and leaves it again, in both directions, to the
    // surrounding WPF controls. The keys come through the DevTools protocol, which reaches the page without the
    // window being in the foreground; WebView2 raises MoveFocusRequested for them as for a user's Tab.
    [Test, NotInParallel("WpfSuite", Order = 16)]
    public async Task TabEntersAndLeavesTheChildWebView()
    {
        using var child = new RunicWebView { Height = 200 };
        var before = new Button { Content = "Before" };
        var after = new Button { Content = "After" };
        var panel = new StackPanel();
        panel.Children.Add(before);
        panel.Children.Add(child);
        panel.Children.Add(after);
        var window = new Window { Content = panel, Width = 600, Height = 400, Title = "Runic WPF tab traversal" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var collection = new ServiceCollection();
            collection.AddSingleton<Func<IBridgeTransport, object, IDisposable>>((_, _) => new Attachment());
            await using var services = collection.BuildServiceProvider();
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
            });
            await using var view = await services.CreateWpfViewAsync(host, new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("<!doctype html><script src=\"webui.js\"></script>"
                    + "<input id=\"first\" aria-label=\"First\"><input id=\"last\" aria-label=\"Last\">"),
            }, new object());
            await view.OpenAsync();
            var controller = child.Controller ?? throw new InvalidOperationException("The child presentation has no WebView2 controller.");

            // The script's result returns after the element has focus, so the key that follows cannot overtake it: the
            // bridge socket and the DevTools protocol are not ordered with each other.
            async Task FocusInPageAsync(string id)
            {
                var focused = await view.Surface.ExecuteJavaScriptAsync(
                    $"document.getElementById('{id}').focus(); return document.activeElement?.id || '-';", TimeSpan.FromSeconds(15));
                Program.Check(focused == id, $"The document could not focus {id}: {focused}.");
            }

            async Task LeftToAsync(Button target)
            {
                try { await UntilAsync(() => FocusManager.GetFocusedElement(window) == target); }
                catch (TimeoutException)
                {
                    var page = await view.Surface.ExecuteJavaScriptAsync("return document.activeElement?.id || '-';", TimeSpan.FromSeconds(15));
                    throw new InvalidOperationException(
                        $"Tab did not leave the document to {target.Content}: WPF focus is {FocusManager.GetFocusedElement(window)}, the page's is {page}.");
                }
            }

            // In, forward: WPF's traversal from the preceding button enters the document's first tab stop.
            before.Focus();
            Program.Check(before.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)),
                "WPF could not move focus from the preceding button into the child presentation.");
            var focus = await view.Surface.ExecuteJavaScriptAsync(FocusProbe("first"), TimeSpan.FromSeconds(15));
            Program.Check(focus.StartsWith("first ", StringComparison.Ordinal), $"Tab did not enter the document's first stop: {focus}.");

            // Out, forward: Tab on the last stop moves WPF focus to the following button.
            await FocusInPageAsync("last");
            await TabAsync(controller, shift: false);
            await LeftToAsync(after);

            // In, backward: Shift+Tab from the following button enters the document's last tab stop.
            Program.Check(after.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous)),
                "WPF could not move focus from the following button into the child presentation.");
            focus = await view.Surface.ExecuteJavaScriptAsync(FocusProbe("last"), TimeSpan.FromSeconds(15));
            Program.Check(focus.StartsWith("last ", StringComparison.Ordinal), $"Shift+Tab did not enter the document's last stop: {focus}.");

            // Out, backward: Shift+Tab on the first stop moves WPF focus to the preceding button.
            await FocusInPageAsync("first");
            await TabAsync(controller, shift: true);
            await LeftToAsync(before);
            Console.WriteLine("Runic.Application.Views.Wpf tab-in/tab-out checks passed.");
        }
        finally { window.Close(); }

        static async Task TabAsync(Runic.Desktop.Internal.WindowsWebView2Controller controller, bool shift)
        {
            // As a keyboard sends them: Shift down, Tab with the Shift modifier (8), Shift up.
            var held = shift ? 8 : 0;
            if (shift) await KeyAsync("rawKeyDown", "Shift", "ShiftLeft", 16);
            await KeyAsync("rawKeyDown", "Tab", "Tab", 9);
            await KeyAsync("keyUp", "Tab", "Tab", 9);
            if (shift) await KeyAsync("keyUp", "Shift", "ShiftLeft", 16, modifiers: 0);

            Task<string> KeyAsync(string type, string key, string code, int virtualKey, int? modifiers = null) =>
                controller.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    $$"""{"type":"{{type}}","modifiers":{{modifiers ?? held}},"key":"{{key}}","code":"{{code}}","windowsVirtualKeyCode":{{virtualKey}},"nativeVirtualKeyCode":{{virtualKey}}}""");
        }
    }

    // SEC-04: the child WebView2 applies the default DesktopWebContentPolicy. Scripts without a user gesture cannot leave
    // the application or open windows; a click, sent through the DevTools protocol as trusted input, opens an external
    // link with the system's handler, which the check replaces to observe it. Developer tools are off outside
    // Development.
    [Test, NotInParallel("WpfSuite", Order = 19)]
    public async Task ChildWebViewKeepsThePageOnTheApplication()
    {
        using var child = new RunicWebView();
        var window = new Window { Content = child, Width = 600, Height = 400, Title = "Runic WPF web content" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        var opened = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var opener = Runic.Desktop.Internal.WindowsWebView2Interop.OpenExternally;
        Runic.Desktop.Internal.WindowsWebView2Interop.OpenExternally = opened.Enqueue;
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var collection = new ServiceCollection();
            collection.AddSingleton<Func<IBridgeTransport, object, IDisposable>>((_, _) => new Attachment());
            await using var services = collection.BuildServiceProvider();
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
            });
            await using var view = await services.CreateWpfViewAsync(host, new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Html("""
                    <!doctype html><script src="webui.js"></script><script>window.documentId = Math.random();</script>
                    <style>body { margin: 0 } a { position: absolute; left: 0; width: 300px; height: 80px; display: block }</style>
                    <a id="out" href="https://example.invalid/clicked" style="top: 0">External</a>
                    <a id="blank" href="https://example.invalid/blank" target="_blank" style="top: 100px">New window</a>
                    """),
            }, new object());
            await view.OpenAsync();
            var controller = child.Controller ?? throw new InvalidOperationException("The child presentation has no WebView2 controller.");
            var expectedDevTools = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
            Program.Check(controller.AreDevToolsEnabled == expectedDevTools, $"Developer tools are {controller.AreDevToolsEnabled}, not {expectedDevTools}.");
            var page = await view.Surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", TimeSpan.FromSeconds(15));

            await view.Surface.ExecuteJavaScriptAsync("location.href = 'https://example.invalid/script'; return true;", TimeSpan.FromSeconds(15));
            var popup = await view.Surface.ExecuteJavaScriptAsync("return String(window.open('https://example.invalid/popup') === null);", TimeSpan.FromSeconds(15));
            await Task.Delay(TimeSpan.FromSeconds(1));
            var after = await view.Surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", TimeSpan.FromSeconds(15));
            Program.Check(after == page && popup == "true" && opened.IsEmpty,
                $"A script left the page or opened a window: {page} -> {after}, window.open null: {popup}, opened: {string.Join(", ", opened)}.");

            await ClickAsync(controller, 50, 40);
            await ClickAsync(controller, 50, 140);
            await UntilAsync(() => opened.Count >= 2);
            await Task.Delay(TimeSpan.FromSeconds(1));
            after = await view.Surface.ExecuteJavaScriptAsync("return location.href + ' ' + window.documentId;", TimeSpan.FromSeconds(15));
            Program.Check(after == page && opened.SequenceEqual(["https://example.invalid/clicked", "https://example.invalid/blank"]),
                $"Clicked external links did not open externally while the page stayed: {page} -> {after}, opened: {string.Join(", ", opened)}.");

            await view.Surface.ExecuteJavaScriptAsync("location.href = location.pathname + '?same-origin'; return true;", TimeSpan.FromSeconds(15));
            // The reloaded page reconnects; ask until it answers from the new document.
            var search = "";
            for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); search != "?same-origin" && DateTime.UtcNow < deadline;)
            {
                try { search = await view.Surface.ExecuteJavaScriptAsync("return location.search;", TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (error is not OutOfMemoryException) { await Task.Delay(100); }
            }
            Program.Check(search == "?same-origin", $"A same-origin navigation did not load: '{search}'.");
            Console.WriteLine("Runic.Application.Views.Wpf web content checks passed.");
        }
        finally
        {
            Runic.Desktop.Internal.WindowsWebView2Interop.OpenExternally = opener;
            window.Close();
        }

        static async Task ClickAsync(Runic.Desktop.Internal.WindowsWebView2Controller controller, int x, int y)
        {
            foreach (var type in new[] { "mousePressed", "mouseReleased" })
                await controller.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                    $$"""{"type":"{{type}}","x":{{x}},"y":{{y}},"button":"left","buttons":1,"clickCount":1}""");
        }
    }

    // RunicViewHost in XAML terms: ViewModel bound to the DataContext, Services inherited from the window.
    [Test, NotInParallel("WpfSuite", Order = 12)]
    public async Task HostAsync()
    {
        List<Attachment> attachments = [];
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<IBridgeTransport, HostedModel, IDisposable>>((_, _) =>
        {
            var attachment = new Attachment();
            attachments.Add(attachment);
            return attachment;
        });
        await using var services = collection.BuildServiceProvider();
        await using var noBridges = new ServiceCollection().BuildServiceProvider();
        var model = new HostedModel();
        var fallback = new TextBlock { Text = "Native editor" };
        var host = Host(fallback);
        AutomationProperties.SetName(host, "Web editor");
        var window = new Window { Content = host, Width = 600, Height = 400, Title = "RunicViewHost smoke", DataContext = model };
        // RunicViewHost shares the inherited Services property of the navigation ViewHost: one provider serves both.
        ViewHost.SetServices(window, services);
        Program.Check(ReferenceEquals(RunicViewHost.ServicesProperty, ViewHost.ServicesProperty) && ReferenceEquals(host.Services, services),
            "RunicViewHost did not share the Services property of ViewHost.");
        window.Show();
        try
        {
            var opened = await SettledAsync(host);
            Program.Check(opened == RunicViewHostState.Open && host.Surface is not null && host.Child is RunicWebView,
                $"The host did not open on Loaded: {host.State} {host.Error}");
            // The web View's pane carries the host's accessible name for screen readers.
            var pane = UIElementAutomationPeer.CreatePeerForElement((RunicWebView)host.Child);
            Program.Check(pane.GetName() == "Web editor" && pane.GetAutomationControlType() == AutomationControlType.Pane,
                $"The web View's automation peer is not the named pane: '{pane.GetName()}' {pane.GetAutomationControlType()}.");
            var first = host.Surface;
            await host.ReloadAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(host.State == RunicViewHostState.Open && !ReferenceEquals(first, host.Surface) && attachments is [{ Disposed: true }, { Disposed: false }],
                "Reload did not replace the session after releasing the first one.");

            // vwpf-06: the token stops the wait, not the reload.
            var reloaded = host.Surface;
            using (var stop = new CancellationTokenSource())
            {
                stop.Cancel();
                var waited = host.ReloadAsync(stop.Token);
                try
                {
                    await waited;
                    Program.Check(false, "A cancelled ReloadAsync wait completed instead of stopping.");
                }
                catch (OperationCanceledException) { }
            }
            await host.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(host.State == RunicViewHostState.Open && !ReferenceEquals(reloaded, host.Surface)
                && attachments.Count == 3 && attachments.Count(item => !item.Disposed) == 1,
                "Cancelling the ReloadAsync wait stopped the reload.");

            // Two swaps in one turn collapse: one fresh session for the latest model.
            window.DataContext = new HostedModel();
            window.DataContext = model;
            await host.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(host.State == RunicViewHostState.Open && attachments.Count(item => !item.Disposed) == 1,
                $"Swaps left {attachments.Count(item => !item.Disposed)} sessions open.");

            window.Content = null;
            // WPF raises Unloaded after layout, not during the removal.
            await UntilAsync(() => !host.IsLoaded);
            await host.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(host.State == RunicViewHostState.Closed && host.Surface is null && host.Child is null && attachments.All(item => item.Disposed),
                "Unloading the host did not release its session, Desktop host and native child.");

            // No Bridge for the model: the fallback presents the same model.
            RunicViewHost.SetServices(window, noBridges);
            var failing = Host(fallback);
            window.Content = failing;
            var failed = await SettledAsync(failing);
            // WPF raises Loaded for the fallback after layout, not when the host shows it; the check below decides.
            try { await UntilAsync(() => fallback.IsLoaded); } catch (TimeoutException) { }
            Program.Check(failed == RunicViewHostState.Failed && failing.Error is InvalidOperationException && fallback.IsLoaded
                && ReferenceEquals(fallback.DataContext, model), $"The host did not fall back: {failing.State} {failing.Error}");
            window.Content = null;
            await UntilAsync(() => !failing.IsLoaded);
            await failing.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(failing.Child is null && fallback.Parent is null, "Unloading did not remove the fallback.");

            // A per-window scope disposed before the host unloads: the unload must still release, without
            // reading the disposed provider (#250).
            var scope = services.CreateAsyncScope();
            RunicViewHost.SetServices(window, scope.ServiceProvider);
            var scoped = Host(fallback);
            window.Content = scoped;
            Program.Check(await SettledAsync(scoped) == RunicViewHostState.Open, $"The scoped host did not open: {scoped.State} {scoped.Error}");
            await scope.DisposeAsync();
            window.Content = null;
            await UntilAsync(() => !scoped.IsLoaded);
            await scoped.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(scoped.State == RunicViewHostState.Closed && scoped.Child is null && attachments.All(item => item.Disposed),
                "Unloading after its services were disposed did not release the host.");
            Console.WriteLine("RunicViewHost open/reload/swap/unload/fallback/disposed-services checks passed.");
        }
        finally { window.Close(); }

        static RunicViewHost Host(object fallback)
        {
            var host = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), Fallback = fallback };
            host.SetBinding(RunicViewHost.ViewModelProperty, new Binding());
            return host;
        }
    }

    // vwpf-08: a WPF window's platform services are unavailable until it has a handle, owned by it while it is open,
    // and closed with it. The clipboard is the service a test can use without a person: a CI desktop may hold the
    // clipboard elsewhere, which reports ResourceBusy or PermissionDenied rather than the owner being unavailable.
    [Test, NotInParallel("WpfSuite", Order = 17)]
    public async Task ForWindowBindsPlatformServicesToTheWpfWindow()
    {
        var window = new Window { Width = 320, Height = 200, Title = "Runic WPF platform services", ShowActivated = false };
        var services = WpfPlatformServices.ForWindow(window);
        var dialogs = services.FileDialogs;
        var clipboard = services.Clipboard;
        Program.Check(await clipboard.ReadTextAsync(64) is PlatformResult<string?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "The services of a window without a handle did not report the owner unavailable.");
        Program.Check(services.GetSnapshot().Statuses.Values.All(status => status is CapabilityStatus.Unavailable),
            "The services of a window without a handle reported an available capability.");
        try
        {
            window.Show();
            var snapshot = services.GetSnapshot();
            Program.Check(snapshot.Statuses.TryGetValue("platform.clipboard.readText", out var read) && read is CapabilityStatus.Available
                && snapshot.Statuses.TryGetValue("platform.files.open", out var open) && open is CapabilityStatus.Available,
                $"The open window's services did not report the Windows provider's capabilities: {snapshot}.");
            var text = $"Runic WPF {Guid.NewGuid():N}";
            // From a thread-pool thread: the services run the native call on the window's dispatcher.
            var written = await Task.Run(async () => await clipboard.WriteTextAsync(text));
            if (written is PlatformResult<PlatformUnit>.Success)
            {
                var copied = await clipboard.ReadTextAsync(text.Length);
                Program.Check(copied is PlatformResult<string?>.Success { Value: var value } && value == text,
                    $"The clipboard did not return the text written through the WPF window: {copied}.");
            }
            else Program.Check(written is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.ResourceBusy or PlatformFailureCode.PermissionDenied },
                $"Writing the clipboard through the open WPF window failed: {written}.");
            Program.Check(ReferenceEquals(dialogs, services.FileDialogs), "The services replaced their file dialogs when the window opened.");
        }
        finally { window.Close(); }
        await UntilAsync(() => services.GetSnapshot().Statuses.Values.All(status => status is CapabilityStatus.Unavailable));
        Program.Check(await clipboard.ReadTextAsync(64) is PlatformResult<string?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The services of a closed window did not report the owner closed.");
        Program.Check(await dialogs.OpenFileAsync(new OpenFileOptions()) is PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The file dialog of a closed window did not report the owner closed.");
        var offThread = await Task.Run(() =>
        {
            try { WpfPlatformServices.ForWindow(window); return null; }
            catch (InvalidOperationException error) { return error; }
        });
        Program.Check(offThread is not null, "ForWindow accepted a call from another thread than the window's.");
        Console.WriteLine("WpfPlatformServices.ForWindow unavailable/bound/closed checks passed.");
    }

    // D09 two-host smoke: two RunicViewHosts in one WPF window and a Runic Desktop window, each with its own Desktop
    // host and session, open together in one process; closing one leaves the others connected to their own models.
    [Test, NotInParallel("WpfSuite", Order = 18)]
    public async Task TwoHostsAndADesktopWindowShareOneProcess()
    {
        Dictionary<HostedModel, Attachment> attachments = [];
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<IBridgeTransport, HostedModel, IDisposable>>((_, model) =>
        {
            var attachment = new Attachment();
            lock (attachments) attachments.Add(model, attachment);
            return attachment;
        });
        await using var services = collection.BuildServiceProvider();
        HostedModel left = new(), right = new(), desktopModel = new();
        var leftHost = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), ViewModel = left, Height = 150 };
        var rightHost = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), ViewModel = right, Height = 150 };
        var panel = new StackPanel();
        panel.Children.Add(leftHost);
        panel.Children.Add(rightHost);
        var window = new Window { Content = panel, Width = 600, Height = 400, Title = "Runic WPF two hosts" };
        RunicViewHost.SetServices(window, services);
        window.Show();
        try
        {
            // The Desktop window: WpfBridgeView over a DesktopHost with its own WebView2 window and thread.
            await using var desktop = await DesktopHost.StartAsync(new() { ConnectionTimeout = TimeSpan.FromSeconds(20) });
            await using var desktopView = await services.CreateWpfViewAsync(desktop, Program.HtmlOptions(), desktopModel);
            await desktopView.OpenAsync();
            Program.Check(await SettledAsync(leftHost) == RunicViewHostState.Open && await SettledAsync(rightHost) == RunicViewHostState.Open,
                $"The two hosts did not open together: {leftHost.State} {leftHost.Error} / {rightHost.State} {rightHost.Error}");
            DesktopSurface[] surfaces = [leftHost.Surface!, rightHost.Surface!, desktopView.Surface];
            Program.Check(surfaces.Select(surface => surface.Url.Port).Distinct().Count() == 3,
                "The hosts did not each serve their presentation from their own Desktop host.");
            lock (attachments)
                Program.Check(attachments.Count == 3 && attachments.Values.All(item => !item.Disposed),
                    "Each host and the Desktop window did not attach one session to its own model.");
            await ConnectedAsync(surfaces);

            // Closing the Desktop window releases its session only.
            await desktopView.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            lock (attachments)
                Program.Check(attachments[desktopModel].Disposed && !attachments[left].Disposed && !attachments[right].Disposed,
                    "Closing the Desktop window released another host's session.");
            Program.Check(leftHost.State == RunicViewHostState.Open && rightHost.State == RunicViewHostState.Open,
                "Closing the Desktop window closed a WPF host.");
            await ConnectedAsync([leftHost.Surface!, rightHost.Surface!]);

            // Unloading one host leaves the other open and connected.
            panel.Children.Remove(leftHost);
            await UntilAsync(() => !leftHost.IsLoaded);
            await leftHost.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            lock (attachments)
                Program.Check(leftHost.State == RunicViewHostState.Closed && attachments[left].Disposed && !attachments[right].Disposed
                    && rightHost.State == RunicViewHostState.Open, "Unloading one host released or closed the other.");
            await ConnectedAsync([rightHost.Surface!]);
            Console.WriteLine("Two RunicViewHosts and a Desktop window in one process: open, isolated close and unload checks passed.");
        }
        finally { window.Close(); }
        await UntilAsync(() => !rightHost.IsLoaded);
        await rightHost.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
        lock (attachments)
            Program.Check(attachments.Values.All(item => item.Disposed), "Closing the WPF window left a session open.");

        // Each document answers through its own surface, so the sessions are live, not just created.
        static async Task ConnectedAsync(IEnumerable<DesktopSurface> surfaces)
        {
            foreach (var surface in surfaces)
            {
                var origin = await surface.ExecuteJavaScriptAsync("return location.origin;", TimeSpan.FromSeconds(20));
                Program.Check(origin == surface.Url.GetLeftPart(UriPartial.Authority),
                    $"The document of {surface.Url} did not answer from its own origin: {origin}.");
            }
        }
    }

    // A WPF window's services announce through its UI Automation peer once it has a handle, and report why they cannot
    // before that and after it closed, through IWindowPlatformServices and through the Desktop priority alike.
    [Test, NotInParallel("WpfSuite", Order = 22)]
    public async Task ForWindowAnnouncesThroughTheWpfWindow()
    {
        var window = new Window { Width = 320, Height = 200, Title = "Runic WPF announcements", ShowActivated = false };
        var services = WpfPlatformServices.ForWindow(window);
        Program.Check(await services.AnnounceAsync("Saved") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A window without a handle did not report the owner unavailable for an announcement.");
        try
        {
            window.Show();
            var polite = await services.AnnounceAsync("Saved");
            // From a thread-pool thread: the announcement runs on the window's dispatcher.
            var assertive = await Task.Run(async () => await services.AnnounceAsync("The disk is full", PlatformAnnouncementPriority.Assertive));
            var desktop = await ((Runic.Application.Views.Desktop.DesktopWindowPlatformServices)services)
                .AnnounceAsync("Copied", DesktopAnnouncementPriority.Assertive);
            Program.Check(polite is PlatformResult<PlatformUnit>.Success && assertive is PlatformResult<PlatformUnit>.Success
                && desktop is PlatformResult<PlatformUnit>.Success,
                $"The open WPF window did not announce: {polite}, {assertive}, {desktop}.");
            try
            {
                await services.AnnounceAsync(" ");
                Program.Check(false, "An empty announcement was accepted.");
            }
            catch (ArgumentException) { }
        }
        finally { window.Close(); }
        Program.Check(await services.AnnounceAsync("Late") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "A closed window did not report the owner closed for an announcement.");
        Console.WriteLine("WpfPlatformServices.ForWindow announcement checks passed.");
    }

    // I-203: while the page of a RunicWebView in the window has the keyboard focus, the window's services announce in
    // the page's live region, which its Bridge added when the page connected, as Runic Desktop's WebView2 window does
    // (I-184); NVDA ignores the WPF window's notification then. Otherwise the WPF window announces, and the page's
    // regions stay empty. The page holds no other script: the Bridge adds the regions without evaluating any.
    [Test, NotInParallel("WpfSuite", Order = 27)]
    public async Task ForWindowAnnouncesInTheFocusedPage()
    {
        using var child = new RunicWebView { Height = 200 };
        var outside = new TextBox { Height = 30 };
        var panel = new StackPanel();
        panel.Children.Add(outside);
        panel.Children.Add(child);
        var window = new Window { Content = panel, Width = 600, Height = 400, Title = "Runic WPF page announcements" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        var platform = WpfPlatformServices.ForWindow(window);
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var collection = new ServiceCollection();
            collection.AddSingleton<Func<IBridgeTransport, object, IDisposable>>((_, _) => new Attachment());
            await using var services = collection.BuildServiceProvider();
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
            });
            await using var view = await services.CreateWpfViewAsync(host, Program.HtmlOptions(), new object());
            await view.OpenAsync();

            // Each region's lines, once both regions exist: "polite=a|b;assertive=c".
            async Task<string> RegionsAsync(string? until = null)
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (true)
                {
                    var regions = await view.Surface.ExecuteJavaScriptAsync("""
                        const arrived = performance.now();
                        while (document.querySelectorAll("[data-runic-announcer]").length < 2 && performance.now() - arrived < 10000)
                          await new Promise(resolve => setTimeout(resolve, 20));
                        return ["polite", "assertive"].map(politeness => {
                          const region = document.querySelector(`[data-runic-announcer="${politeness}"]`);
                          return politeness + "=" + (region ? [...region.children].map(line => line.textContent).join("|") : "missing");
                        }).join(";");
                        """, TimeSpan.FromSeconds(15));
                    if (until is null || regions == until || DateTime.UtcNow > deadline) return regions;
                    await Task.Delay(100);
                }
            }

            Program.Check(await RegionsAsync() == "polite=;assertive=", $"The connected page did not get empty live regions: {await RegionsAsync()}.");

            // The keyboard focus in the WPF window: the WPF window announces.
            Program.Check(outside.Focus() && Keyboard.FocusedElement == outside, "The WPF text box did not take the keyboard focus.");
            Program.Check(child.FocusedPageAnnouncer is null, "The page was reported to have the focus while a WPF control had it.");
            Program.Check(await platform.AnnounceAsync("Outside") is PlatformResult<PlatformUnit>.Success,
                "The WPF window did not announce while a WPF control had the focus.");

            // The keyboard focus in the page: the page's regions announce, a repeated message as a new line.
            Program.Check(((System.Windows.Interop.IKeyboardInputSink)child).TabInto(new TraversalRequest(FocusNavigationDirection.Next)),
                "WPF could not tab into the child presentation.");
            var focus = await view.Surface.ExecuteJavaScriptAsync(FocusProbe("editor"), TimeSpan.FromSeconds(15));
            Program.Check(focus.StartsWith("editor ", StringComparison.Ordinal), $"The page could not focus its editor: {focus}.");
            await UntilAsync(() => child.FocusedPageAnnouncer is not null);
            var polite = await platform.AnnounceAsync("Saved");
            var again = await platform.AnnounceAsync("Saved");
            // From a thread-pool thread: the services find the focused page on the window's dispatcher.
            var assertive = await Task.Run(async () => await platform.AnnounceAsync("The disk is full", PlatformAnnouncementPriority.Assertive));
            Program.Check(polite is PlatformResult<PlatformUnit>.Success && again is PlatformResult<PlatformUnit>.Success
                && assertive is PlatformResult<PlatformUnit>.Success, $"The page did not announce: {polite}, {again}, {assertive}.");
            const string Said = "polite=Saved|Saved;assertive=The disk is full";
            var regions = await RegionsAsync(Said);
            Program.Check(regions == Said, $"The page's live regions did not receive the announcements once each: {regions}.");

            // Back in the WPF window: the WPF window announces again.
            Program.Check(outside.Focus(), "The WPF text box did not take the keyboard focus back.");
            await UntilAsync(() => child.FocusedPageAnnouncer is null);
            Program.Check(await platform.AnnounceAsync("Outside again") is PlatformResult<PlatformUnit>.Success,
                "The WPF window did not announce after the focus left the page.");
            await Task.Delay(500);
            regions = await RegionsAsync();
            Program.Check(regions == Said, $"The page announced while the WPF window had the focus: {regions}.");
            Console.WriteLine("WpfPlatformServices.ForWindow page announcement checks passed.");
        }
        finally { window.Close(); }
    }

    // B10: a WPF window's services show WPF context menus: Windows access keys, shortcut hints, check marks and disabled
    // entries; the chosen item, null when dismissed or replaced by a later menu, cancellation, and the owner's state.
    [Test, NotInParallel("WpfSuite", Order = 25)]
    public async Task ForWindowShowsWpfContextMenus()
    {
        var window = new Window { Width = 400, Height = 300, Title = "Runic WPF context menus", ShowActivated = false };
        var services = WpfPlatformServices.ForWindow(window);
        System.Collections.Immutable.ImmutableArray<DesktopMenuEntry> items =
        [
            new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7", Shortcut = DesktopShortcut.Parse("F2"), AccessKey = 'R' },
            new DesktopMenuItem("Pinned", "row.pin") { IsChecked = true },
            new DesktopMenuSeparator(),
            new DesktopMenu("More", [new DesktopMenuItem("Delete_all", "row.delete") { IsEnabled = false }]),
        ];
        Program.Check(await services.ShowContextMenuAsync(items) is PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A window without a handle did not report the owner unavailable for a context menu.");
        var opened = new System.Collections.Concurrent.ConcurrentQueue<ContextMenu>();
        Runic.Application.Views.Wpf.WpfContextMenu.Opened = opened.Enqueue;
        try
        {
            window.Show();
            var snapshot = services.GetSnapshot();
            Program.Check(snapshot.Statuses.TryGetValue(PlatformCapabilityIds.ContextMenu, out var context) && context is CapabilityStatus.Available
                && snapshot.Statuses.TryGetValue(PlatformCapabilityIds.MenuBar, out var bar) && bar is CapabilityStatus.Unavailable,
                $"The WPF window's services did not report context menus without a menu bar: {snapshot}.");
            try
            {
                await services.ShowContextMenuAsync([]);
                Program.Check(false, "An empty context menu was accepted.");
            }
            catch (ArgumentException) { }

            // Chosen: the first item.
            var chosen = services.ShowContextMenuAsync(items).AsTask();
            var menu = await NextMenuAsync(opened);
            var rename = (MenuItem)menu.Items[0];
            var pinned = (MenuItem)menu.Items[1];
            var more = (MenuItem)menu.Items[3];
            Program.Check(Equals(rename.Header, "_Rename") && rename.InputGestureText == "F2" && rename.IsEnabled && !rename.IsChecked,
                $"Rename shows '{rename.Header}' '{rename.InputGestureText}'.");
            Program.Check(pinned.IsChecked && menu.Items[2] is Separator, "Pinned is checked and the separator follows it.");
            var delete = (MenuItem)more.Items[0];
            Program.Check(!delete.IsEnabled && Equals(delete.Header, "Delete__all"), $"Delete shows '{delete.Header}' and is enabled: {delete.IsEnabled}.");
            rename.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Program.Check(await chosen.WaitAsync(TimeSpan.FromSeconds(10)) is PlatformResult<DesktopMenuItem?>.Success { Value: var item }
                && ReferenceEquals(item, items[0]), "Choosing Rename did not return it.");

            // Dismissed: null.
            var dismissed = services.ShowContextMenuAsync(items).AsTask();
            (await NextMenuAsync(opened)).IsOpen = false;
            Program.Check(await dismissed.WaitAsync(TimeSpan.FromSeconds(10)) is PlatformResult<DesktopMenuItem?>.Success { Value: null },
                "A dismissed menu did not return null.");

            // A later menu replaces the open one, which returns null; cancelling closes the later one.
            var first = services.ShowContextMenuAsync(items).AsTask();
            await NextMenuAsync(opened);
            using var cancel = new CancellationTokenSource();
            var second = services.ShowContextMenuAsync(items, new DesktopContextMenuOptions { X = 10, Y = 20 }, cancel.Token).AsTask();
            var later = await NextMenuAsync(opened);
            Program.Check(await first.WaitAsync(TimeSpan.FromSeconds(10)) is PlatformResult<DesktopMenuItem?>.Success { Value: null },
                "A menu replaced by a later one did not return null.");
            Program.Check(later.IsOpen, "The later menu is not open.");
            cancel.Cancel();
            try
            {
                await second.WaitAsync(TimeSpan.FromSeconds(10));
                Program.Check(false, "A cancelled context menu did not throw OperationCanceledException.");
            }
            catch (OperationCanceledException) { }
            Program.Check(!later.IsOpen, "The cancelled menu stayed open.");
        }
        finally
        {
            Runic.Application.Views.Wpf.WpfContextMenu.Opened = null;
            window.Close();
        }
        Program.Check(await services.ShowContextMenuAsync(items) is PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "A closed window did not report the owner closed for a context menu.");
        Console.WriteLine("WpfPlatformServices.ForWindow context menu checks passed.");
    }

    // B10: a RunicWebView's Desktop window shows WPF context menus at a page point of the control.
    [Test, NotInParallel("WpfSuite", Order = 26)]
    public async Task ChildWebViewShowsContextMenusAtAPagePoint()
    {
        using var child = new RunicWebView();
        var window = new Window { Content = child, Width = 600, Height = 400, Title = "Runic WPF child context menu" };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        child.Loaded += (_, _) => loaded.TrySetResult();
        var opened = new System.Collections.Concurrent.ConcurrentQueue<ContextMenu>();
        Runic.Application.Views.Wpf.WpfContextMenu.Opened = opened.Enqueue;
        window.Show();
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var host = await DesktopHost.StartAsync(new()
            {
                WindowHostFactory = child.CreateWindowHostFactory(),
                ConnectionTimeout = TimeSpan.FromSeconds(20),
                WaitForConnection = false,
            });
            await using var surface = await host.CreateSurfaceAsync(Program.HtmlOptions());
            await using var desktop = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded });
            Program.Check(desktop.Capabilities.HasFlag(DesktopWindowCapabilities.ContextMenu) && !desktop.Capabilities.HasFlag(DesktopWindowCapabilities.MenuBar)
                && desktop.MenuBarPlacement == DesktopMenuBarPlacement.None,
                $"The child window reports {desktop.Capabilities} and placement {desktop.MenuBarPlacement}.");
            System.Collections.Immutable.ImmutableArray<DesktopMenuEntry> items = [new DesktopMenuItem("Open", "row.open")];
            var chosen = desktop.ShowContextMenuAsync(items, new DesktopContextMenuOptions { X = 40, Y = 30 }).AsTask();
            var menu = await NextMenuAsync(opened);
            var zoom = child.Controller?.ZoomFactor ?? 1;
            Program.Check(menu.Placement == System.Windows.Controls.Primitives.PlacementMode.Relative && ReferenceEquals(menu.PlacementTarget, child)
                && Math.Abs(menu.HorizontalOffset - 40 * zoom) < 0.01 && Math.Abs(menu.VerticalOffset - 30 * zoom) < 0.01,
                $"The menu opens at {menu.Placement} {menu.HorizontalOffset},{menu.VerticalOffset}, not at the page point.");
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Program.Check(ReferenceEquals(await chosen.WaitAsync(TimeSpan.FromSeconds(10)), items[0]), "Choosing Open did not return it.");
        }
        finally
        {
            Runic.Application.Views.Wpf.WpfContextMenu.Opened = null;
            window.Close();
        }
        Console.WriteLine("RunicWebView context menu checks passed.");
    }

    private static async Task<ContextMenu> NextMenuAsync(System.Collections.Concurrent.ConcurrentQueue<ContextMenu> opened)
    {
        ContextMenu? menu = null;
        await UntilAsync(() => opened.TryDequeue(out menu));
        return menu!;
    }

    // RunicViewHost.WebContent reaches the child WebView2: developer tools follow it, and a change reopens.
    [Test, NotInParallel("WpfSuite", Order = 23)]
    public async Task HostAppliesItsWebContent()
    {
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<IBridgeTransport, HostedModel, IDisposable>>((_, _) => new Attachment());
        await using var services = collection.BuildServiceProvider();
        var expectedDefault = string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);
        var host = new RunicViewHost
        {
            SurfaceOptions = Program.HtmlOptions(),
            ViewModel = new HostedModel(),
            WebContent = new DesktopWebContentPolicy { DevTools = !expectedDefault },
        };
        var window = new Window { Content = host, Width = 600, Height = 400, Title = "RunicViewHost web content" };
        RunicViewHost.SetServices(window, services);
        window.Show();
        try
        {
            Program.Check(await SettledAsync(host) == RunicViewHostState.Open, $"The host did not open: {host.State} {host.Error}");
            var controller = ((RunicWebView)host.Child).Controller ?? throw new InvalidOperationException("The host's WebView has no controller.");
            Program.Check(controller.AreDevToolsEnabled == !expectedDefault,
                $"The host's WebContent did not reach the WebView: developer tools are {controller.AreDevToolsEnabled}.");
            var first = host.Surface;
            host.WebContent = null;
            await host.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(host.State == RunicViewHostState.Open && !ReferenceEquals(first, host.Surface),
                "Changing WebContent did not reopen the presentation.");
            controller = ((RunicWebView)host.Child).Controller ?? throw new InvalidOperationException("The host's WebView has no controller.");
            Program.Check(controller.AreDevToolsEnabled == expectedDefault,
                $"A host without WebContent did not apply the default policy: developer tools are {controller.AreDevToolsEnabled}.");
            Console.WriteLine("RunicViewHost.WebContent checks passed.");
        }
        finally { window.Close(); }
    }

    // SEC-06 and D040: RunicViewHost.ContentSecurityPolicy reaches WebView2, and the Bridge works under a policy without
    // 'unsafe-eval': the page's inline script and string evaluation are blocked, while its same-origin script calls the
    // View's routes and receives state the Desktop transport publishes. ExecuteJavaScriptAsync is an eval API and is
    // refused. A value with a line break is rejected. The WebView holds the surface's only client, so the check reads
    // the page rather than the response headers.
    [Test, NotInParallel("WpfSuite", Order = 31)]
    public async Task HostSendsItsContentSecurityPolicy()
    {
        const string Policy = "default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var collection = new ServiceCollection();
        // The page reaches the window over the Views channel: it completes its hello, asks the probe's reserved route to
        // broadcast a push, and reports what it received.
        collection.AddSingleton<Func<IBridgeTransport, HostedModel, IDisposable>>((transport, _) =>
            BridgeDispatcher.For(transport).RegisterRoute("runic.csp-probe", new CspProbeRoute(received)));
        await using var services = collection.BuildServiceProvider();
        var host = new RunicViewHost
        {
            SurfaceOptions = new DesktopSurfaceOptions
            {
                Content = new DesktopContent.Handler((request, _) => ValueTask.FromResult<ContentResponse?>(request.Path switch
                {
                    "/" or "/index.html" => new ContentResponse(System.Text.Encoding.UTF8.GetBytes("""
                        <!doctype html><title>csp</title><script src="webui.js"></script><script src="csp.js"></script>
                        <script>document.title = 'inline script ran';</script>
                        """), "text/html; charset=utf-8"),
                    "/csp.js" => new ContentResponse(System.Text.Encoding.UTF8.GetBytes("""
                        const request = message => webui.call('__runicViews', JSON.stringify(message));
                        globalThis.__runicDesktopViews.onMessage(text => {
                          const state = JSON.parse(text);
                          let evaluation = 'allowed';
                          try { new Function('return 1')(); } catch (error) { evaluation = error.name; }
                          request({ kind: 'csp-received', route: 'runic.csp-probe',
                            text: `${state.value}:${state.text}:${evaluation}:${document.title}` }).catch(() => {});
                        });
                        webui.connected
                          .then(() => request({ kind: 'hello', protocol: 4, session: 'csp', client: { name: 'csp', version: '0' } }))
                          .then(() => request({ kind: 'csp-ready', route: 'runic.csp-probe' }))
                          .catch(() => {});
                        """), "text/javascript; charset=utf-8"),
                    _ => null,
                })),
            },
            ViewModel = new HostedModel(),
            ContentSecurityPolicy = Policy,
        };
        var rejected = false;
        try { host.ContentSecurityPolicy = "default-src 'self'\r\nX-Injected: 1"; }
        catch (ArgumentException) { rejected = true; }
        Program.Check(rejected && host.ContentSecurityPolicy == Policy, "A policy with a line break was accepted.");
        var window = new Window { Content = host, Width = 600, Height = 400, Title = "RunicViewHost content security policy" };
        RunicViewHost.SetServices(window, services);
        window.Show();
        try
        {
            Program.Check(await SettledAsync(host) == RunicViewHostState.Open, $"The host did not open: {host.State} {host.Error}");
            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(result == "42:Grüße:EvalError:csp",
                $"The page under the policy reported '{result}' instead of the published state, refused evaluation and its own title.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var refused = false;
            try { await host.Surface!.ExecuteJavaScriptAsync("return document.title;", cancellationToken: deadline.Token); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("JavaScript execution failed", StringComparison.Ordinal))
            {
                refused = true;
            }
            Program.Check(refused, "ExecuteJavaScriptAsync ran under a policy without 'unsafe-eval'.");
            Console.WriteLine("RunicViewHost.ContentSecurityPolicy checks passed.");
        }
        finally { window.Close(); }
    }

    // D034.7: hosts given one application-owned DesktopHost serve their surfaces from its listener; unloading one
    // releases only its surface, and a host without one keeps its own. A disposed shared host fails the open.
    [Test, NotInParallel("WpfSuite", Order = 24)]
    public async Task HostsShareAnApplicationOwnedDesktopHost()
    {
        Dictionary<HostedModel, Attachment> attachments = [];
        var collection = new ServiceCollection();
        collection.AddSingleton<Func<IBridgeTransport, HostedModel, IDisposable>>((_, model) =>
        {
            var attachment = new Attachment();
            lock (attachments) attachments.Add(model, attachment);
            return attachment;
        });
        await using var services = collection.BuildServiceProvider();
        var shared = await DesktopHost.StartAsync(new() { ConnectionTimeout = TimeSpan.FromSeconds(20) });
        HostedModel left = new(), right = new(), own = new();
        var leftHost = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), ViewModel = left, Desktop = shared, Height = 120 };
        var rightHost = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), ViewModel = right, Desktop = shared, Height = 120 };
        var ownHost = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), ViewModel = own, Height = 120 };
        var panel = new StackPanel();
        panel.Children.Add(leftHost);
        panel.Children.Add(rightHost);
        panel.Children.Add(ownHost);
        var window = new Window { Content = panel, Width = 600, Height = 450, Title = "Runic WPF shared Desktop host" };
        RunicViewHost.SetServices(window, services);
        window.Show();
        try
        {
            Program.Check(await SettledAsync(leftHost) == RunicViewHostState.Open && await SettledAsync(rightHost) == RunicViewHostState.Open
                && await SettledAsync(ownHost) == RunicViewHostState.Open,
                $"The hosts did not open: {leftHost.State} {leftHost.Error} / {rightHost.State} {rightHost.Error} / {ownHost.State} {ownHost.Error}");
            Program.Check(leftHost.Surface!.Url.Port == shared.Port && rightHost.Surface!.Url.Port == shared.Port
                && !ReferenceEquals(leftHost.Surface, rightHost.Surface),
                "The hosts given the shared Desktop host did not each create a surface on its listener.");
            Program.Check(ownHost.Surface!.Url.Port != shared.Port, "A host without a Desktop host did not start its own.");
            await ConnectedAsync([leftHost.Surface!, rightHost.Surface!, ownHost.Surface!]);

            panel.Children.Remove(leftHost);
            await UntilAsync(() => !leftHost.IsLoaded);
            await leftHost.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            lock (attachments)
                Program.Check(leftHost.State == RunicViewHostState.Closed && attachments[left].Disposed && !attachments[right].Disposed
                    && rightHost.State == RunicViewHostState.Open, "Unloading one host on the shared Desktop host released or closed the other.");
            await ConnectedAsync([rightHost.Surface!]);
            await using (var surface = await shared.CreateSurfaceAsync(Program.HtmlOptions()))
                Program.Check(surface.Url.Port == shared.Port, "Unloading a host disposed the shared Desktop host.");

            panel.Children.Remove(rightHost);
            await UntilAsync(() => !rightHost.IsLoaded);
            await rightHost.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await shared.DisposeAsync();
            var fallback = new TextBlock { Text = "Native editor" };
            var late = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), ViewModel = new HostedModel(), Desktop = shared, Fallback = fallback, Height = 120 };
            panel.Children.Add(late);
            Program.Check(await SettledAsync(late) == RunicViewHostState.Failed && late.Error is ObjectDisposedException,
                $"A host given a disposed Desktop host did not fail: {late.State} {late.Error}");
            Console.WriteLine("RunicViewHost shared Desktop host checks passed.");
        }
        finally
        {
            window.Close();
            await shared.DisposeAsync();
        }

        static async Task ConnectedAsync(IEnumerable<DesktopSurface> surfaces)
        {
            foreach (var surface in surfaces)
            {
                var origin = await surface.ExecuteJavaScriptAsync("return location.origin;", TimeSpan.FromSeconds(20));
                Program.Check(origin == surface.Url.GetLeftPart(UriPartial.Authority),
                    $"The document of {surface.Url} did not answer from its own origin: {origin}.");
            }
        }
    }

    // Resolves when the document's focused element has the given id, or after ten seconds, and reports the element
    // it settled on, the element it found on arrival, the wait, and whether the page had OS focus. It polls
    // because Chromium does not raise focusin in a page without OS focus, while activeElement still moves.
    private static string FocusProbe(string id) => $$"""
        const arrived = performance.now();
        const initial = document.activeElement?.id || "-";
        while (document.activeElement?.id !== "{{id}}" && performance.now() - arrived < 10000)
          await new Promise(resolve => setTimeout(resolve, 5));
        return `${document.activeElement?.id || "-"} initial=${initial} waitedMs=${Math.round(performance.now() - arrived)} hasFocus=${document.hasFocus()}`;
        """;

    private static async Task<RunicViewHostState> SettledAsync(RunicViewHost host)
    {
        await UntilAsync(() => host.State is not (RunicViewHostState.Closed or RunicViewHostState.Opening));
        await host.WaitForPresentationAsync();
        return host.State;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The RunicViewHost check timed out.");
            await Task.Delay(20);
        }
    }

    private sealed class HostedModel;
    private sealed class Bindings(params IDisposable[] bindings) : IDisposable
    {
        public void Dispose()
        {
            foreach (var binding in bindings) binding.Dispose();
        }
    }
    private sealed class Attachment : IDisposable
    {
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { Disposed = true; Released.TrySetResult(); }
    }
}

// The reserved route of HostSendsItsContentSecurityPolicy's page: csp-ready broadcasts a push, csp-received reports it.
file sealed class CspProbeRoute(TaskCompletionSource<string> received) : IBridgeRouteHandler
{
    public async ValueTask<ReadOnlyMemory<byte>> HandleAsync(BridgeRouteRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind == "csp-ready")
            await request.Dispatcher.BroadcastAsync("{\"value\":42,\"text\":\"Grüße\"}"u8.ToArray(), cancellationToken);
        else if (request.Kind == "csp-received")
            received.TrySetResult(request.Root.GetProperty("text").GetString()!);
        return BridgeMessages.Ok;
    }
}
