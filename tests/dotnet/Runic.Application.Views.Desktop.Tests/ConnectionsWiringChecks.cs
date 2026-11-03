using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using TUnit.Core;

// IRunicConnections on Runic Desktop: OpenWindowAsync binds the Window scope's service to the Window's own session when
// RunicWindowOpening.AttachBridge attaches its Bridge, the step RunicCsWebUiHost shares. The Desktop transport's keys
// never repeat, so its dispatcher uses them unchanged.
internal sealed class ConnectionsWiringChecks
{
    [Test, NotInParallel(Order = 19)]
    public async Task OpenWindowAsyncBindsTheWindowScopesConnectionsToItsDesktopSession()
    {
        var services = new ServiceCollection();
        services.AddScoped<ConnectionsProbeViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, ConnectionsProbeViewModel, IDisposable>>(
            _ => static (_, _, _) => new NoopAttachment());
        services.AddRunicWindowService();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });

        await using var first = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<ConnectionsProbeWindow>();
        await using var second = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<ConnectionsProbeWindow>();
        var firstConnections = (RunicConnections)first.ViewModel.Connections;
        var secondConnections = (RunicConnections)second.ViewModel.Connections;
        Require(ReferenceEquals(firstConnections, first.Services.GetRequiredService<IRunicConnections>()),
            "The ViewModel did not take the Window scope's IRunicConnections.");
        Require(firstConnections.Session is { } firstSession && secondConnections.Session is { } secondSession
            && !ReferenceEquals(firstSession, secondSession),
            "OpenWindowAsync did not bind each Window's IRunicConnections to its own session.");
        Require(firstConnections.Session!.Dispatcher.Transport is DesktopBridgeTransport and IBridgeUniqueConnectionKeys,
            "The Desktop Window's connections do not follow its Desktop transport.");
        Require(first.ViewModel.Connections.Caller is null, "A Desktop Window reported a caller outside a command.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

sealed class ConnectionsProbeViewModel(IRunicConnections connections) : INotifyPropertyChanged
{
    public IRunicConnections Connections { get; } = connections;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class ConnectionsProbeWindow : RunicWindow<ConnectionsProbeViewModel>;
