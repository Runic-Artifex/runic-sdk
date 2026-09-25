using CsWebUi;
using FirstWindow;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi;

var services = new ServiceCollection();
var probeFactoryFailure = args.Contains("--probe-factory-failure", StringComparer.Ordinal);
var scopeDisposals = 0;
if (probeFactoryFailure)
{
    services.AddScoped(_ => new ScopeProbe(() => scopeDisposals++));
    services.AddScoped(provider =>
    {
        _ = provider.GetRequiredService<ScopeProbe>();
        return new CounterViewModel();
    });
}
else services.AddScoped<CounterViewModel>();
services.AddRunicBridges();
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
if (probeFactoryFailure)
{
    try
    {
        _ = provider.OpenWindow<CounterWindow, CounterViewModel>(
            _ => throw new InvalidOperationException("Expected Window construction failure."));
        throw new InvalidOperationException("The Window factory unexpectedly succeeded.");
    }
    catch (InvalidOperationException error) when (error.Message == "Expected Window construction failure.")
    {
        if (scopeDisposals != 1)
            throw new InvalidOperationException($"Failed Window construction released the scope {scopeDisposals} times.");
        Console.WriteLine("FIRST_WINDOW_FACTORY_FAILURE_OK");
        WebUiApplication.Clean();
        return;
    }
}
using var window = provider.OpenWindow<CounterWindow, CounterViewModel>(host => new CounterWindow(host));
if (args.Contains("--probe-window-close", StringComparer.Ordinal))
{
    var close = await window.CloseAsync(TimeSpan.Zero);
    await close.Completion;
    if (!close.Drained || close.RemainingOperations != 0)
        throw new InvalidOperationException("An idle Window did not close cleanly.");
    await window.DisposeAsync();
    Console.WriteLine("FIRST_WINDOW_CLOSE_OK");
    WebUiApplication.Clean();
    return;
}
window.SetRootFolder(Path.Combine(AppContext.BaseDirectory, "www"));
if (args.Contains("--serve-only", StringComparer.Ordinal))
{
    Console.WriteLine(window.StartServer("index.html"));
    Console.Out.Flush();
    await Console.In.ReadLineAsync();
}
else
{
    window.Show("index.html");
    WebUiApplication.Wait();
}
WebUiApplication.Clean();

internal sealed class ScopeProbe(Action onDispose) : IDisposable
{
    public void Dispose() => onDispose();
}
