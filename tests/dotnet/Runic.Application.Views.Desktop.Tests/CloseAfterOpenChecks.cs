using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// The case captures what the switch prints to standard output and error, and restores the writers in finally
// blocks; the cases do not run in parallel (AssemblyInfo.cs), so no other case writes meanwhile.
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging

// RUNIC_APPLICATION_CLOSE_AFTER_OPEN lets a harness start an unchanged Desktop application: the
// opened presentation is reported on standard output and closed, so WaitForCloseAsync returns. An active
// switch is announced on standard error and logged as event 2002.
internal sealed class CloseAfterOpenChecks
{
    [Test, NotInParallel(Order = 1)]
    public async Task CloseAfterOpenSwitchReportsAndClosesTheWindow()
    {
        string? previous = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN");
        var console = Console.Out;
        var errorConsole = Console.Error;
        try
        {
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", null);
            var quiet = await OpenAsync();
            Check(quiet.IsOpen && !quiet.Output.Contains("RUNIC_APPLICATION_OPENED", StringComparison.Ordinal)
                && quiet.Error.Length == 0 && quiet.Events.Count == 0,
                "Without the automation variable the window must stay open and print and log nothing.");

            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", "1");
            var closed = await OpenAsync();
            Check(!closed.IsOpen, "RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 did not close the opened window.");
            Check(closed.Output.Contains("RUNIC_APPLICATION_OPENED=Embedded", StringComparison.Ordinal),
                $"RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 did not report the presentation: '{closed.Output}'.");
            Check(closed.Error.Contains("warning: RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1", StringComparison.Ordinal),
                $"RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 did not warn on standard error: '{closed.Error}'.");
            Check(closed.Events.SequenceEqual([2002]), "RUNIC_APPLICATION_CLOSE_AFTER_OPEN=1 was not logged as event 2002.");
        }
        finally
        {
            Console.SetOut(console);
            Console.SetError(errorConsole);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", previous);
        }
    }

    private static async Task<(bool IsOpen, string Output, string Error, List<int> Events)> OpenAsync()
    {
        var logger = new EventRecorder();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(logger);
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
        var error = new StringWriter();
        var console = Console.Out;
        var errorConsole = Console.Error;
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<ProbeWindow>();
            bool isOpen = ((DesktopBridgeWindow<OrderedCollectionModel>)window.Host).Presentation.IsOpen;
            if (!isOpen)
            {
                // The application's own wait returns at once for a closed presentation.
                await window.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            return (isOpen, output.ToString(), error.ToString(), logger.Events);
        }
        finally
        {
            Console.SetOut(console);
            Console.SetError(errorConsole);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Records the IDs of Runic.Application.Views.Desktop warnings.
    private sealed class EventRecorder : ILoggerFactory, ILogger
    {
        public List<int> Events { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) lock (Events) Events.Add(eventId.Id);
        }
        public void Dispose() { }
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
        public ValueTask ResizeAsync(int width, int height, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask MoveAsync(int x, int y, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetVisibleAsync(bool visible, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask BeginMoveAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() { _open = false; return ValueTask.CompletedTask; }
    }
}
