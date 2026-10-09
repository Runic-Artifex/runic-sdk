using Microsoft.Extensions.DependencyInjection;
using OperationsConsumer;
using Runic.Application.Views;
using Runic.Desktop;
using Runic.Navigation.ReactiveUI;

var services = new ServiceCollection();
services.AddRunicReactiveModelContext();
services.AddScoped<OperationsViewModel>();
services.AddRunicBridges();
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false });
var scope = provider.CreateAsyncScope();
await using var surface = await desktop.CreateSurfaceAsync(new DesktopSurfaceOptions
{
    Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html"),
});
var transport = new DesktopBridgeTransport(surface);
var model = scope.ServiceProvider.GetRequiredService<OperationsViewModel>();
using var content = new WindowContentSession(transport, rootModel: model);
using var attachment = scope.ServiceProvider.GetRequiredService<
    Func<IBridgeTransport, WindowContentSession, OperationsViewModel, IDisposable>>()(transport, content, model);
Console.WriteLine($"OPERATIONS_CONSUMER_URL={surface.Url}");
Console.Out.Flush();

// The test driver owns the simulated repository recovery release. Once close
// stops bridge admission, recovery still finishes through its real service task.
if (await Console.In.ReadLineAsync() != "close")
    throw new InvalidOperationException("Expected the driver to close after the browser journey.");
if (!model.MutationActive || !model.RecoveryStarted || model.RecoveryPublished)
    throw new InvalidOperationException("Expected cancelled invocation with accepted recovery still running.");
var close = await content.BeginCloseAsync(TimeSpan.FromSeconds(5));
await close.Completion;
if (!close.Drained) throw new InvalidOperationException("The generated invocation wrapper did not drain.");
Console.WriteLine("OPERATIONS_CONTENT_CLOSE_COMPLETED");
var disposal = scope.DisposeAsync().AsTask();
if (disposal.IsCompleted || model.Disposed)
    throw new InvalidOperationException("Scope disposal released resources before accepted recovery finished.");
Console.WriteLine("OPERATIONS_DISPOSE_PENDING");
Console.Out.Flush();
if (await Console.In.ReadLineAsync() != "release")
    throw new InvalidOperationException("Expected the driver to release accepted recovery.");
model.ReleaseRecovery();
await disposal.WaitAsync(TimeSpan.FromSeconds(10));
if (!model.Disposed || !model.RecoveryPublished || model.MutationActive)
    throw new InvalidOperationException("Disposal did not await the actual accepted work.");
Console.WriteLine("OPERATIONS_ACCEPTED_WORK_DRAINED");
Console.Out.Flush();
