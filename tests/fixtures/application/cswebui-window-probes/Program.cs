using CommunityToolkit.Mvvm.ComponentModel;
using CsWebUi;
using CsWebUiWindowProbes;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views.CsWebUi;

// Each probe prints one marker that CI matches.
switch (args.SingleOrDefault())
{
    case "--probe-factory-failure":
        ProbeFactoryFailure();
        break;
    case "--probe-window-close":
        await ProbeWindowCloseAsync();
        break;
    default:
        Console.Error.WriteLine("Usage: CsWebUiWindowProbes --probe-factory-failure | --probe-window-close");
        return 2;
}
WebUiApplication.Clean();
return 0;

// A Window factory failure must release the Window scope exactly once.
static void ProbeFactoryFailure()
{
    var services = new ServiceCollection();
    var scopeDisposals = 0;
    services.AddScoped(_ => new ScopeProbe(() => scopeDisposals++));
    services.AddScoped(provider =>
    {
        _ = provider.GetRequiredService<ScopeProbe>();
        return new ProbeViewModel();
    });
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    try
    {
        _ = provider.OpenWindow<ProbeWindow, ProbeViewModel>(
            _ => throw new InvalidOperationException("Expected Window construction failure."));
        throw new InvalidOperationException("The Window factory unexpectedly succeeded.");
    }
    catch (InvalidOperationException error) when (error.Message == "Expected Window construction failure.")
    {
        if (scopeDisposals != 1)
            throw new InvalidOperationException($"Failed Window construction released the scope {scopeDisposals} times.");
        Console.WriteLine("FIRST_WINDOW_FACTORY_FAILURE_OK");
    }
}

// An idle Window closes without remaining operations and disposes cleanly.
static async Task ProbeWindowCloseAsync()
{
    var services = new ServiceCollection();
    services.AddScoped<ProbeViewModel>();
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var window = provider.OpenWindow<ProbeWindow, ProbeViewModel>(host => new ProbeWindow(host));
    var close = await window.CloseAsync(TimeSpan.Zero);
    await close.Completion;
    if (!close.Drained || close.RemainingOperations != 0)
        throw new InvalidOperationException("An idle Window did not close cleanly.");
    await window.DisposeAsync();
    Console.WriteLine("FIRST_WINDOW_CLOSE_OK");
}

namespace CsWebUiWindowProbes
{
    public sealed partial class ProbeViewModel : ObservableObject
    {
        [ObservableProperty] private int count;
    }

    public sealed partial class ProbeWindow(CsWebUiBridgeWindow<ProbeViewModel> host)
        : CsWebUiWindow<ProbeViewModel>(host);

    internal sealed class ScopeProbe(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
