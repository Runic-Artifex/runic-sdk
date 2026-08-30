using Runic.Desktop;

await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = "<title>Windows package consumer</title>",
});

if (!surface.Url.IsLoopback)
{
    throw new InvalidOperationException("The clean Windows package consumer did not create a private loopback surface.");
}

Console.WriteLine("Clean Windows package consumer built from the Runic Desktop API only.");
