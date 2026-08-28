using Runic.Desktop;

await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    ContentHandler = static (request, _) => ValueTask.FromResult<ContentResponse?>(
        request.Path == "/probe" ? ContentResponse.Text("package-ok", "text/plain") : null),
});

using var client = new HttpClient();
var result = await client.GetStringAsync(new Uri(surface.Url, "probe"));
if (result != "package-ok")
{
    throw new InvalidOperationException($"Unexpected package-consumer response: {result}");
}

Console.WriteLine($"Runic.Desktop package consumer passed at {surface.Url}");
