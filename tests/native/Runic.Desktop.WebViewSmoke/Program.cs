using Runic.Desktop;

if (!DesktopPlatform.IsEmbeddedWindowAvailable)
{
    throw new PlatformNotSupportedException("The platform embedded WebView runtime is not available.");
}

if (OperatingSystem.IsMacOS())
{
    RunMacOsSmoke();
}
else
{
    await RunAsyncSmoke();
}

Console.WriteLine("Runic Desktop embedded WebView smoke passed.");

static async Task RunAsyncSmoke()
{
    await using var host = await DesktopHost.StartAsync(CreateHostOptions());
    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = FirstPage() }))
    await using (var window = await surface.OpenWindowAsync(FirstWindowOptions()))
    {
        await ExerciseFirstWindowAsync(surface, window);
        await window.CloseAsync();
        AssertClosed(window);
    }

    await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = RestartedPage() }))
    await using (var window = await surface.OpenWindowAsync(new DesktopWindowOptions
    {
        Browser = BrowserKind.Embedded,
        Hidden = true,
    }))
    {
        await ExerciseRestartedWindowAsync(surface);
    }
}

static void RunMacOsSmoke()
{
    var host = DesktopHost.StartAsync(CreateHostOptions()).AsTask().GetAwaiter().GetResult();
    try
    {
        RunMacOsWindow(host, FirstPage(), FirstWindowOptions(), ExerciseFirstWindowAsync);
        RunMacOsWindow(
            host,
            RestartedPage(),
            new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = true },
            static (surface, _) => ExerciseRestartedWindowAsync(surface));
    }
    finally
    {
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static void RunMacOsWindow(
    DesktopHost host,
    string content,
    DesktopWindowOptions options,
    Func<DesktopSurface, DesktopWindow, Task> exercise)
{
    var surface = host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = content })
        .AsTask().GetAwaiter().GetResult();
    DesktopWindow? window = null;
    try
    {
        window = surface.OpenWindowAsync(options).AsTask().GetAwaiter().GetResult();
        var activeWindow = window;
        var work = Task.Run(async () =>
        {
            try
            {
                await exercise(surface, activeWindow);
            }
            finally
            {
                await activeWindow.CloseAsync();
            }
        });
        activeWindow.WaitForClose();
        work.GetAwaiter().GetResult();
        AssertClosed(activeWindow);
    }
    finally
    {
        window?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        surface.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static async Task ExerciseFirstWindowAsync(DesktopSurface surface, DesktopWindow window)
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
}

static async Task ExerciseRestartedWindowAsync(DesktopSurface surface)
{
    if (await surface.ExecuteJavaScriptAsync("return document.title;", TimeSpan.FromSeconds(10)) != "restarted")
    {
        throw new InvalidOperationException("The embedded WebView did not restart.");
    }
}

static void AssertClosed(DesktopWindow window)
{
    if (window.IsOpen || window.NativeHandle != 0)
    {
        throw new InvalidOperationException("The embedded platform window did not close.");
    }
}

static DesktopHostOptions CreateHostOptions() => new()
{
    DiagnosticSink = diagnostic => Console.Error.WriteLine(
        $"Desktop diagnostic: {diagnostic.Category}/{diagnostic.Code}: {diagnostic.Message}"),
};

static DesktopWindowOptions FirstWindowOptions() => new()
{
    Browser = BrowserKind.Embedded,
    Width = 640,
    Height = 480,
    MinimumWidth = 320,
    MinimumHeight = 240,
    Hidden = true,
};

static string FirstPage() =>
    """
    <!doctype html><html><head><meta charset="utf-8"><script src="webui.js"></script>
    <title>Runic Desktop M6 smoke</title></head><body>embedded</body></html>
    """;

static string RestartedPage() =>
    """
    <!doctype html><html><head><script src="webui.js"></script>
    <title>restarted</title></head><body></body></html>
    """;
