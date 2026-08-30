using System;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application;

[assembly: RunicApplicationManifest("runic-examples-hello-lifecycle", Version = "1.0.0", Provenance = "example")]

await using ApplicationHost application = RunicApplication.CreateBuilder(args)
    .UseHost(new ConsoleApplicationHost())
    .Build();
await application.RunAsync();
return 0;

internal sealed class ConsoleApplicationHost : IApplicationHost
{
    public ValueTask StartAsync(
        ApplicationCompositionManifest manifest,
        ReadOnlyMemory<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine("Preparing the workspace...");
        Console.WriteLine("Hello from Runic Application!");
        Console.WriteLine($"Running {manifest.EntryPoint}.");
        return ValueTask.CompletedTask;
    }

    public ValueTask WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Closing the workspace...");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
