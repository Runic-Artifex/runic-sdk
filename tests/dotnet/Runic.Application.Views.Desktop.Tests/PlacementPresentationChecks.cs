using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// sdk#897, W1b: a ViewModel's IRunicWindowPresentation maximizes, restores and places its Runic Desktop window through
// DesktopWindow's own requests, in DesktopWindowState.Placement's units, and reports the window's placement.
internal sealed class PlacementPresentationChecks
{
    private static readonly DesktopWindowState Opened = new()
    {
        X = 10, Y = 20, Width = 800, Height = 600,
        Placement = new DesktopWindowPlacement { X = 10, Y = 20, Width = 800, Height = 600, Origin = DesktopCoordinateOrigin.TopLeft },
    };

    [Test, NotInParallel(Order = 46)]
    public async Task APresentationMaximizesRestoresAndPlacesItsWindow()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialState = Opened, Places = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<PresentationWindow>();
        var presentation = window.Presentation;
        var host = factory.Hosts[0];
        Require(presentation.Features.HasFlag(RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement),
            $"Features: {presentation.Features}.");
        await WaitUntilAsync(() => presentation.Placement == new RunicWindowPlacement { X = 10, Y = 20, Width = 800, Height = 600 });

        var changes = 0;
        presentation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IRunicWindowPresentation.Placement)) Interlocked.Increment(ref changes);
        };
        Require(await presentation.MaximizeAsync() is PlatformResult<PlatformUnit>.Success, "Maximizing failed.");
        await WaitUntilAsync(() => presentation.Placement is { Maximized: true, Width: 800 });
        Require(await presentation.RestoreAsync() is PlatformResult<PlatformUnit>.Success, "Restoring failed.");
        await WaitUntilAsync(() => presentation.Placement is { Maximized: false });
        Require(Volatile.Read(ref changes) >= 2, "PropertyChanged did not report the placement changes.");

        // A placement saved from the window's state passes back without copying fields.
        var saved = new DesktopWindowPlacement { X = 5, Y = 6, Width = 900, Height = 700, Maximized = true }.ToRunicPlacement();
        Require(await presentation.SetPlacementAsync(saved) is PlatformResult<PlatformUnit>.Success, "Placing failed.");
        Require(host.ShowStates.SequenceEqual<object>(["maximize", "restore", new DesktopWindowPlacement
            {
                X = 5, Y = 6, Width = 900, Height = 700, Maximized = true, Origin = DesktopCoordinateOrigin.TopLeft,
            }]), $"The window was asked {string.Join(", ", host.ShowStates)}.");
        await WaitUntilAsync(() => presentation.Placement == saved);
        await RequireThrowsAsync<ArgumentException>(() => presentation.SetPlacementAsync(new RunicWindowPlacement { X = 1 }), "A placement with X alone");
        await RequireThrowsAsync<ArgumentNullException>(() => presentation.SetPlacementAsync(null!), "A null placement");

        await DesktopPlatformServices.GetDesktopWindow(window)!.CloseAsync();
        await window.WaitForCloseAsync();
        Require(await presentation.MaximizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A closed window did not report the owner closed.");
        Require(presentation.Placement == saved, "The placement is not readable after the window closed.");
        await window.DisposeAsync();
    }

    [Test, NotInParallel(Order = 46)]
    public async Task AWindowHostThatCannotPlaceReportsBackendUnavailable()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialState = Opened };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<PresentationWindow>();
        var presentation = window.Presentation;
        Require((presentation.Features & (RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement)) == 0,
            $"A host without WindowPlacement reports {presentation.Features}.");
        Require(presentation.Placement is null, "A host without WindowPlacement reports a placement.");
        foreach (var result in new[]
        {
            await presentation.MaximizeAsync(), await presentation.RestoreAsync(),
            await presentation.SetPlacementAsync(new RunicWindowPlacement { Width = 640, Height = 480 }),
        })
        {
            Require(result is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                $"A host without WindowPlacement answered {result}.");
        }
    }

    [Test, NotInParallel(Order = 46)]
    public async Task APositionOnWaylandIsRefusedAsAWhole()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialState = Opened, Places = true, PositionUnavailable = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<PresentationWindow>();
        var presentation = window.Presentation;
        Require(await presentation.SetPlacementAsync(new RunicWindowPlacement { X = 1, Y = 2, Width = 640, Height = 480 })
            is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "A position on Wayland was not refused as backend-unavailable.");
        Require(factory.Hosts[0].ShowStates.IsEmpty, "The refused placement reached the window.");
        Require(await presentation.SetPlacementAsync(new RunicWindowPlacement { Width = 640, Height = 480 }) is PlatformResult<PlatformUnit>.Success,
            "A placement without a position failed on Wayland.");
        await WaitUntilAsync(() => presentation.Placement is { Width: 640, Height: 480, X: 10 });
    }

    [Test, NotInParallel(Order = 46)]
    public void PlacementsConvertBetweenTheLayers()
    {
        var desktop = new DesktopWindowPlacement { X = -1920, Y = 40, Width = 1280, Height = 720, Maximized = true, FullScreen = true, Origin = DesktopCoordinateOrigin.TopLeft };
        var runic = desktop.ToRunicPlacement();
        Require(runic == new RunicWindowPlacement { X = -1920, Y = 40, Width = 1280, Height = 720, Maximized = true, FullScreen = true },
            $"ToRunicPlacement gave {runic}.");
        Require(runic.ToDesktopPlacement() == desktop, "The placement did not round-trip.");
        Require(new RunicWindowPlacement { Width = 800, Height = 600 }.ToDesktopPlacement() is { X: null, Y: null, Origin: DesktopCoordinateOrigin.TopLeft },
            "A placement without a position does not keep it.");
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.WindowPlacement, open: true, macOS: false)
            == (RunicWindowPresentationFeatures.Maximize | RunicWindowPresentationFeatures.Placement), "WindowPlacement is not Maximize and Placement.");
        RequireThrows<ArgumentNullException>(() => ((DesktopWindowPlacement)null!).ToRunicPlacement(), "ToRunicPlacement(null)");
        RequireThrows<ArgumentNullException>(() => ((RunicWindowPlacement)null!).ToDesktopPlacement(), "ToDesktopPlacement(null)");
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<PresentationViewModel>();
        services.AddScoped<Func<IBridgeTransport, PresentationViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddRunicWindowService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static async Task RequireThrowsAsync<TException>(Func<ValueTask<PlatformResult<PlatformUnit>>> call, string what) where TException : Exception
    {
        try { await call(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{what} did not throw {typeof(TException).Name}.");
    }

    private static void RequireThrows<TException>(Func<object?> action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{what} did not throw {typeof(TException).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
