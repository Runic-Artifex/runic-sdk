using Runic.Desktop;

if (!DesktopPlatform.IsEmbeddedWindowAvailable)
{
    throw new PlatformNotSupportedException("The platform embedded WebView runtime is not available.");
}

await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
{
    DiagnosticSink = diagnostic => Console.Error.WriteLine(
        $"Desktop diagnostic: {diagnostic.Category}/{diagnostic.Code}: {diagnostic.Message}"),
});
await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = """
        <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
        <title>Runic Desktop M6 smoke</title></head><body>embedded</body></html>
        """,
}))
await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    Browser = BrowserKind.Embedded,
    Width = 640,
    Height = 480,
    MinimumWidth = 320,
    MinimumHeight = 240,
    Hidden = true,
}))
{
    if (window.NativeHandle == 0)
    {
        throw new InvalidOperationException("The embedded platform window did not open.");
    }
    if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) !=
        "Runic Desktop M6 smoke")
    {
        throw new InvalidOperationException("The embedded WebView bridge did not execute JavaScript.");
    }

    await window.ResizeAsync(700, 500);
    await window.MoveAsync(20, 30);
    await window.FocusAsync();
    await window.MinimizeAsync();
    await window.ToggleMaximizedAsync();
    await window.CloseAsync();
    if (window.IsOpen || window.NativeHandle != 0)
    {
        throw new InvalidOperationException("The embedded platform window did not close.");
    }
}

await using (var restarted = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = """
        <!doctype html><html><head><script src="webui.js"></script>
        <title>restarted</title></head><body></body></html>
        """,
}))
await using (var restartedWindow = await restarted.OpenWindowAsync(new DesktopWindowOptions
{
    Browser = BrowserKind.Embedded,
    Hidden = true,
}))
{
    if (await restarted.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "restarted")
    {
        throw new InvalidOperationException("The embedded WebView did not restart.");
    }
}

Console.WriteLine("Runic Desktop embedded WebView smoke passed.");
