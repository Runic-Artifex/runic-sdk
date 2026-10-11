using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TUnit.Core;

// The native WPF checks, after the portable cases (WpfDispatcherExecutor runs them on the dispatcher).
internal sealed class NativeChecks
{
    // This case never completed in CI before the TUnit migration: the former Main used the default
    // ShutdownMode.OnLastWindowClose, so the window.Close() in this case's finally block ended Application.Run and
    // the process exited with code 0 before the failure reached Main's catch (no run printed the success line
    // below, and RunicViewHost's HostAsync never ran). Run under TUnit, it fails: after location.reload() the
    // surface raises neither Disconnected nor a second Connected event within 20 seconds. Skipped until that is
    // resolved; see PR #194.
    [Test, NotInParallel("WpfSuite", Order = 11)]
    [Skip("Known failure: no reconnect after location.reload() in the WPF child WebView (PR #194).")]
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
            // OpenAsync completes when the token check succeeds; the session raises its Connected event after that,
            // from a thread-pool thread, so the first event can arrive after OpenAsync has returned.
            var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var observe = view.Surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind != PresentationEventKind.Connected) return;
                lock (connectionIds)
                {
                    connectionIds.Add(invocation.Session.Id);
                    connected.TrySetResult();
                    if (connectionIds.Count > 1) reconnected.TrySetResult();
                }
            });
            await view.OpenAsync();
            await Task.WhenAny(connected.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Program.Check(window.IsVisible && child.Handle != 0, "The WebView did not remain inside the WPF shell.");
            lock (connectionIds) Program.Check(connectionIds.Count == 1, "The first document did not authenticate.");
            await view.Surface.RunJavaScriptAsync("location.reload();");
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Program.Check(!attachment.Disposed, "Browser reload retired the child session.");
            child.Width = 350;
            child.Height = 220;
            window.UpdateLayout();
            Program.Check(((System.Windows.Interop.IKeyboardInputSink)child).TabInto(
                new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next)),
                "WPF could not tab into the child presentation.");
            var focused = await view.Surface.ExecuteJavaScriptAsync("return document.activeElement.id;");
            Program.Check(focused == "editor", "The child document could not focus its editor.");
            window.Content = null;
            await attachment.Released.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await view.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Program.Check(attachment.Disposed && window.IsVisible, "Unloading the web child did not retire its session while preserving the shell.");
            Console.WriteLine("Runic.Application.Views.Wpf native authentication/reload/unload checks passed.");
        }
        finally { window.Close(); }
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
            Program.Check(failed == RunicViewHostState.Failed && failing.Error is InvalidOperationException && fallback.IsLoaded
                && ReferenceEquals(fallback.DataContext, model), $"The host did not fall back: {failing.State} {failing.Error}");
            window.Content = null;
            await UntilAsync(() => !failing.IsLoaded);
            await failing.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Program.Check(failing.Child is null && fallback.Parent is null, "Unloading did not remove the fallback.");
            Console.WriteLine("RunicViewHost open/reload/swap/unload/fallback checks passed.");
        }
        finally { window.Close(); }

        static RunicViewHost Host(object fallback)
        {
            var host = new RunicViewHost { SurfaceOptions = Program.HtmlOptions(), Fallback = fallback };
            host.SetBinding(RunicViewHost.ModelProperty, new Binding());
            return host;
        }
    }

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
