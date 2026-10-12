using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// The case replaces Console.Out and Console.In and restores them in finally blocks; the cases do not run in
// parallel (AssemblyInfo.cs), so no other case uses them meanwhile.
#pragma warning disable TUnit0055 // Overwriting the Console writer can break TUnit logging

// RUNIC_APPLICATION_SERVE_ONLY has CS-WebUI's contract on Runic Desktop: the surface serves without a window, its
// URL is written as RUNIC_APPLICATION_URL=<url>, and WaitForCloseAsync returns once a line is read from standard
// input. It takes precedence over RUNIC_APPLICATION_CLOSE_AFTER_OPEN.
internal sealed class ServeOnlyChecks
{
    [Test, NotInParallel(Order = 1)]
    public async Task ServeOnlySwitchServesWithoutAWindowUntilAnInputLine()
    {
        string? serveOnly = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY");
        string? closeAfterOpen = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN");
        var console = Console.Out;
        var input = Console.In;
        var lines = new LineReader();
        var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", "1");
            Console.SetIn(lines);
            Console.SetOut(output);

            var services = new ServiceCollection();
            services.AddScoped<OrderedCollectionModel>();
            services.AddScoped<Func<IBridgeTransport, OrderedCollectionModel, IDisposable>>(
                _ => (transport, model) => new OrderedCollectionBridge(transport, model));
            await using var provider = services.BuildServiceProvider();
            var factory = new NativeDispatchFactory();
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = factory,
                WaitForConnection = false,
            });
            await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<ProbeWindow>();
            var host = (DesktopBridgeWindow<OrderedCollectionModel>)window.Host;

            Check(factory.Hosts.Count == 0, "Serve-only mode opened a window.");
            Check(output.ToString().Contains($"RUNIC_APPLICATION_URL={host.Surface.Url}", StringComparison.Ordinal),
                $"Serve-only mode did not write the surface URL: '{output}'.");
            Check(!output.ToString().Contains("RUNIC_APPLICATION_OPENED", StringComparison.Ordinal),
                "RUNIC_APPLICATION_CLOSE_AFTER_OPEN took precedence over serve-only mode.");
            using (var client = new HttpClient())
            {
                using var page = await client.GetAsync(host.Surface.Url);
                Check(page.IsSuccessStatusCode, $"The serve-only surface did not serve its page: {page.StatusCode}.");
            }

            var closed = window.WaitForCloseAsync().AsTask();
            await Task.Delay(200);
            Check(!closed.IsCompleted, "WaitForCloseAsync returned before a line was read from standard input.");
            lines.Send("\n");
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            lines.Send(null);
            Console.SetOut(console);
            Console.SetIn(input);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", serveOnly);
            Environment.SetEnvironmentVariable("RUNIC_APPLICATION_CLOSE_AFTER_OPEN", closeAfterOpen);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Standard input whose ReadLine blocks until the case sends a line, or null for the end of input.
    private sealed class LineReader : TextReader
    {
        private readonly TaskCompletionSource<string?> _line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Send(string? line) => _line.TrySetResult(line?.TrimEnd('\n'));
        public override string? ReadLine() => _line.Task.GetAwaiter().GetResult();
    }
}
