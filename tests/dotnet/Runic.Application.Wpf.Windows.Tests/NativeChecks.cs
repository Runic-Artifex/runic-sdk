using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using System.Windows;

internal static class NativeChecks
{
    internal static async Task RunAsync()
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
            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var observe = view.Surface.SubscribeConnectionEvents(invocation =>
            {
                if (invocation.Kind != PresentationEventKind.Connected) return;
                lock (connectionIds)
                {
                    connectionIds.Add(invocation.Session.Id);
                    if (connectionIds.Count > 1) reconnected.TrySetResult();
                }
            });
            await view.OpenAsync();
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
            Console.WriteLine("Runic.Application.Wpf native authentication/reload/unload checks passed.");
        }
        finally { window.Close(); }
    }
    private sealed class Attachment : IDisposable
    {
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { Disposed = true; Released.TrySetResult(); }
    }
}
