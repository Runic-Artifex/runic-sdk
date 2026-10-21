using Runic.Desktop;

// Runic Desktop on its own serves content and presents it. For calls between the page and .NET, use
// Runic Windows and Views (Runic.Application.Views.Desktop), whose Bridge runs over the same surface.
var content = """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <script src="webui.js"></script>
      <title>Runic Desktop</title>
    </head>
    <body>
      <h1>Runic Desktop managed presentation host</h1>
      <p id="greeting">Waiting for the application…</p>
      <button id="increment" onclick="setCount(count + 1)">Increment</button>
      <p>Count: <strong id="count">0</strong></p>
      <script>
        let count = 0;
        function setCount(value) { count = value; document.querySelector('#count').textContent = value; }
      </script>
    </body>
    </html>
    """;

// The process main thread runs the platform event loop, which services native window events while the
// application awaits its window.
return DesktopEventLoop.Run(
    new DesktopHostOptions { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } },
    async host =>
    {
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(content) });
        // Each page that connects, including after a reload, gets the greeting from .NET.
        using var connections = surface.SubscribeConnectionEvents(connection =>
        {
            Console.WriteLine($"Page {connection.Kind}; {connection.Surface.ConnectionCount} connected.");
            if (connection.Kind != DesktopConnectionEventKind.Connected) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await surface.ExecuteJavaScriptAsync(
                        "document.querySelector('#greeting').textContent = 'Hello from .NET ' + "
                            + "'" + Environment.Version + "'; return '';",
                        timeout.Token);
                }
                catch (Exception error) when (error is InvalidOperationException or OperationCanceledException)
                {
                    Console.WriteLine($"The page left before the greeting: {error.Message}");
                }
            });
        });

        var browser = args.Contains("--webview", StringComparer.Ordinal)
            ? BrowserKind.Embedded
            : BrowserKind.Any;
        await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = browser,
            Width = 900,
            Height = 650,
        });

        Console.WriteLine($"Runic Desktop surface available at {surface.Url}");
        Console.WriteLine("Close the presentation window to exit.");
        await window.WaitForCloseAsync();
        return 0;
    });
