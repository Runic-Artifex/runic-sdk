using System;
using System.Threading;
using System.Threading.Tasks;
using NativeAotSizeComparison;
using Runic.Desktop;

var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

await using var host = await DesktopHost.StartAsync(new() { Linux = new() { EmbeddedBackend = LinuxEmbeddedBackend.Gtk3WebKit41 } });
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = new DesktopContent.Html(ComparisonContent.ScriptedHtml),
});
using var connections = surface.SubscribeConnectionEvents(connection =>
{
    if (connection.Kind == DesktopConnectionEventKind.Connected) connected.TrySetResult();
});

await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    Browser = BrowserKind.Any,
});
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await connected.Task.WaitAsync(deadline.Token);
if (await surface.ExecuteJavaScriptAsync("return ping('comparison');", deadline.Token) != "pong:comparison")
{
    throw new InvalidOperationException("The page did not answer the comparison ping.");
}
await window.CloseAsync();

Console.WriteLine("Runic Desktop NativeAOT comparison interaction passed.");
