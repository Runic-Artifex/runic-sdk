using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Platform;
using System.Windows;
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
                    events.Add($"{invocation.Kind} {invocation.Session.Id}");
                    if (invocation.Kind != PresentationEventKind.Connected) return;
                    connectionIds.Add(invocation.Session.Id);
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

    // RunicViewHost in XAML terms: Model bound to the DataContext, Services inherited from the window.
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
        var window = new Window { Content = host, Width = 600, Height = 400, Title = "RunicViewHost smoke", DataContext = model };
        RunicViewHost.SetServices(window, services);
        window.Show();
        try
        {
            var opened = await SettledAsync(host);
            Program.Check(opened == RunicViewHostState.Open && host.Surface is not null && host.Child is RunicWebView,
                $"The host did not open on Loaded: {host.State} {host.Error}");
            var first = host.Surface;
            await host.ReloadAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(host.State == RunicViewHostState.Open && !ReferenceEquals(first, host.Surface) && attachments is [{ Disposed: true }, { Disposed: false }],
                "Reload did not replace the session after releasing the first one.");

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
            host.SetBinding(RunicViewHost.ModelProperty, new Binding());
            return host;
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
    private sealed class Attachment : IDisposable
    {
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { Disposed = true; Released.TrySetResult(); }
    }
}
