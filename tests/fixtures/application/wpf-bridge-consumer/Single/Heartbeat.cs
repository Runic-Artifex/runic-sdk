using Microsoft.Extensions.Hosting;

namespace WpfSingle;

// A hosted service. BackgroundService is in Microsoft.AspNetCore.App, which
// Runic.Desktop references, so it is not in the output folder (#270).
public sealed class Heartbeat : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
