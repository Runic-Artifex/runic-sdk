using Runic.Desktop;

await using var window = new WebUiWindow();
window.SetSize(900, 650);

window.Bind("multiply", static e => e.GetInt64() * e.GetInt64(1));
window.BindAsync("greet", static (e, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    return ValueTask.FromResult<WebUiResult>($"Hello, {e.GetString()}!");
});
window.Bind("increment", e =>
{
    var count = int.Parse(
        e.Window.ExecuteJavaScript("return getCount();", TimeSpan.FromSeconds(5)),
        System.Globalization.CultureInfo.InvariantCulture);
    e.RunJavaScript($"setCount({count + 1});");
});

var content = """
    <!doctype html>
    <html lang="en">
    <head>
      <meta charset="utf-8">
      <script src="webui.js"></script>
      <title>Runic Desktop</title>
    </head>
    <body>
      <h1>Runic Desktop is running without the native WebUI library</h1>
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

if (args.Contains("--webview", StringComparer.Ordinal))
{
    Console.WriteLine("Selected host: platform embedded WebView");
    await window.ShowWebViewAsync(content);
}
else
{
    Console.WriteLine($"Selected browser: {window.BestBrowser}");
    await window.ShowInBrowserAsync(content, WebUiBrowser.AnyBrowser);
}

Console.WriteLine($"Runic Desktop window available at {window.Url}");
Console.WriteLine("Press Enter to close.");
Console.ReadLine();
