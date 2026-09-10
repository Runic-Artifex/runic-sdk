using Runic.Desktop;

if (args.Contains("--expect-missing-loader", StringComparer.Ordinal))
{
    var embedded = DesktopPlatform.GetAvailability().Presentations.Single(p => p.Browser == BrowserKind.Embedded);
    if (embedded.IsAvailable || embedded.Diagnostic?.Code != "webview2-loader-unavailable")
        throw new InvalidOperationException($"Expected a loader-specific diagnostic, got {embedded}.");
    Console.WriteLine("Missing loader correctly distinguished from missing Edge runtime.");
    return;
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = "<title>Windows package consumer</title><script src=\"webui.js\"></script>",
});

if (!surface.Url.IsLoopback)
{
    throw new InvalidOperationException("The clean Windows package consumer did not create a private loopback surface.");
}

Console.WriteLine("Clean Windows package consumer built from the Runic Desktop API only.");

if (args.Contains("--embedded-smoke", StringComparer.Ordinal))
{
    await using var window = await surface.OpenWindowAsync(new() { Browser = BrowserKind.Embedded, Hidden = true }, deadline.Token);
    if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(15), cancellationToken: deadline.Token) != "Windows package consumer")
        throw new InvalidOperationException("The packaged WebView2 consumer did not execute JavaScript.");
    await window.CloseAsync();
    Console.WriteLine("Packaged WebView2 loader and live JavaScript smoke passed.");
}
