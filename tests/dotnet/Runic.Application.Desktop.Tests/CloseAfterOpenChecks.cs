using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

// RUNIC_APPLICATION_CLOSE_AFTER_OPEN lets a harness start an unchanged Desktop application: the
// opened presentation is reported on standard output and closed, so WaitForClose returns.
static class CloseAfterOpenChecks
{
    internal static async Task RunAsync()
    {
        string? previous = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN");
        var console = Console.Out;
        try
        {
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", null);
            var (keptOpen, quietOutput) = await OpenAsync();
            Check(keptOpen && !quietOutput.Contains("RUNIC_APPLICATION_OPENED", StringComparison.Ordinal),
                "Without the automation variable the window must stay open and print nothing.");

            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", "1");
            var (stillOpen, output) = await OpenAsync();
            Check(!stillOpen, "RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 did not close the opened window.");
            Check(output.Contains("RUNIC_APPLICATION_OPENED=Embedded", StringComparison.Ordinal),
                $"RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 did not report the presentation: '{output}'.");
        }
        finally
        {
            Console.SetOut(console);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", previous);
        }
    }

    private static async Task<(bool IsOpen, string Output)> OpenAsync()
    {
        var services = new ServiceCollection();
        services.AddScoped<OrderedCollectionModel>();
        services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
            _ => (transport, model) => new OrderedCollectionBridge(transport, model));
        await using var provider = services.BuildServiceProvider();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new HeadlessWindowHostFactory(),
            WaitForConnection = false,
        });
        var output = new StringWriter();
        var console = Console.Out;
        Console.SetOut(output);
        DesktopBridgeWindow<OrderedCollectionModel>? bridge = null;
        try
        {
            await using var window = await provider.OpenDesktopWindowAsync<ProbeWindow, OrderedCollectionModel>(host,
                new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                owner => { bridge = owner; return new ProbeWindow(owner); },
                new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            bool isOpen = bridge!.Presentation.IsOpen;
            if (!isOpen)
            {
                // The application's own wait returns at once for a closed presentation.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                bridge.Presentation.WaitForClose(deadline.Token);
            }
            return (isOpen, output.ToString());
        }
        finally
        {
            Console.SetOut(console);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class HeadlessWindowHostFactory : IDesktopWindowHostFactory
    {
        public bool IsSupported => true;
        public IDesktopWindowHost Create() => new HeadlessWindowHost();
    }

    private sealed class HeadlessWindowHost : IDesktopWindowHost
    {
        private volatile bool _open;
        public event EventHandler? Closed;
        public bool IsOpen => _open;
        public nint NativeHandle => _open ? 0x5252 : 0;
        public ValueTask OpenAsync(Uri url, DesktopWindowHostOptions options, CancellationToken cancellationToken = default)
        { _open = true; return ValueTask.CompletedTask; }
        public ValueTask NavigateAsync(Uri url, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            if (_open) { _open = false; Closed?.Invoke(this, EventArgs.Empty); }
            return ValueTask.CompletedTask;
        }
        public ValueTask FocusAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MinimizeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ToggleMaximizedAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(uint width, uint height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(uint x, uint y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { _open = false; return ValueTask.CompletedTask; }
    }
}
