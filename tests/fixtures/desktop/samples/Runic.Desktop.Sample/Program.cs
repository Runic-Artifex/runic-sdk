using System.Globalization;
using Runic.Desktop;

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
      <button onclick="multiply(6, 7).then(value => alert(`6 × 7 = ${value}`))">Multiply</button>
      <button onclick="greet('Runic Desktop').then(alert)">Greet</button>
      <button id="increment">Increment from managed C#</button>
      <p>Count: <strong id="count">0</strong></p>
      <script>
        let count = 0;
        function getCount() { return count; }
        function setCount(value) { count = value; document.querySelector('#count').textContent = value; }
      </script>
    </body>
    </html>
    """;

await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = content });
using var multiply = surface.RegisterCapability(
    "multiply",
    static (invocation, _) => ValueTask.FromResult<PresentationResult>(
        invocation.GetInt64() * invocation.GetInt64(1)));
using var greet = surface.RegisterCapability(
    "greet",
    static (invocation, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PresentationResult>($"Hello, {invocation.GetString()}!");
    });
using var increment = surface.RegisterCapability(
    "increment",
    async (invocation, cancellationToken) =>
    {
        var count = int.Parse(
            await surface.ExecuteJavaScriptAsync("return getCount();", cancellationToken: cancellationToken),
            CultureInfo.InvariantCulture);
        await invocation.RunJavaScriptAsync($"setCount({count + 1});", cancellationToken);
        return PresentationResult.None;
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
Console.WriteLine("Press Enter to close.");
Console.ReadLine();
