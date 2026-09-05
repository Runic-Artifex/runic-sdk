using System;
using System.Threading;
using System.Threading.Tasks;
using NativeAotSizeComparison;
using Runic.Desktop;

var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

await using var host = await DesktopHost.StartAsync();
await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = ComparisonContent.Html,
});
using var ping = surface.RegisterCapability(
    "ping",
    static (invocation, _) => ValueTask.FromResult<PresentationResult>(
        $"pong:{invocation.GetString()}"));
using var complete = surface.RegisterCapability(
    "complete",
    (invocation, _) =>
    {
        if (invocation.GetString() == "pong:comparison")
        {
            completed.TrySetResult();
        }

        return ValueTask.FromResult(PresentationResult.None);
    });

await using var window = await surface.OpenWindowAsync(new DesktopWindowOptions
{
    Browser = BrowserKind.Any,
});
await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
await window.CloseAsync();

Console.WriteLine("Runic Desktop NativeAOT comparison interaction passed.");
