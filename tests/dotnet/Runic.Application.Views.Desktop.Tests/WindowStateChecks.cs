using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// W315-001: a Window reports its embedded window's state, also after it closed, restores a saved placement through
// RunicDesktopHost.Placement, and a Window without a native owner reports it unavailable.
internal sealed class WindowStateChecks
{
    private static readonly DesktopWindowState Opened = new()
    {
        X = 40, Y = 30, Width = 1024, Height = 700, IsMaximized = true,
        Placement = new() { X = 40, Y = 30, Width = 900, Height = 600, Maximized = true },
    };

    [Test, NotInParallel(Order = 17)]
    public async Task WindowStateIsReadAndPlacementRestoredThroughTheHost()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialState = Opened };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var saved = new DesktopWindowPlacement { X = -2000, Y = 10, Width = 1280, Height = 800, Maximized = true };
        var asked = new List<IRunicWindow>();
        var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
            Placement = window => { asked.Add(window); return saved; },
        });
        var window = await host.OpenWindowAsync<FileWindow>(new RunicWindowOptions { Width = 300, Height = 200 });
        try
        {
            Require(asked.Count == 1 && ReferenceEquals(asked[0], window), "The placement callback was not asked for the opening Window.");
            var options = factory.Hosts[0].Options!;
            Require(options.Placement == saved && options.Width == 1280 && options.Height == 800 && options.X == -2000 && options.Y == 10,
                $"The saved placement did not replace the Window's size and position: {options}.");
            Require(DesktopPlatformServices.GetWindowState(window) is PlatformResult<DesktopWindowState>.Success { Value: var state } && state == Opened,
                "The state of an open embedded Window was not reported.");

            var resized = Opened with { Width = 1100, Placement = Opened.Placement with { Width = 950 } };
            var changed = new TaskCompletionSource<DesktopWindowState>(TaskCreationOptions.RunContinuationsAsynchronously);
            var presentation = ((DesktopBridgeWindow<FileViewModel>)window.Host).Window;
            presentation.StateChanged += (_, args) => changed.TrySetResult(args.State);
            factory.Hosts[0].Report(resized);
            Require(await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)) == resized, "StateChanged did not report the new state.");

            await presentation.CloseAsync();
            await window.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            // The save path: after WaitForCloseAsync, before the Window is disposed.
            Require(DesktopPlatformServices.GetWindowState(window) is PlatformResult<DesktopWindowState>.Success { Value: var last } && last == resized,
                "The last state of a closed Window was not kept.");
        }
        finally
        {
            await window.DisposeAsync();
        }
    }

    [Test, NotInParallel(Order = 18)]
    public async Task WindowsWithoutAStateReportWhy()
    {
        await using (var provider = CreateProvider())
        {
            await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
            await using var window = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<FileWindow>();
            Require(DesktopPlatformServices.GetWindowState(window) is PlatformResult<DesktopWindowState>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "An embedded window host without state did not report the backend unavailable.");
        }

        // A Window on another host, such as CS-WebUI, has no native owner.
        await using (var provider = CreateProvider())
        {
            await using var window = await new ForeignHost(provider).OpenWindowAsync<FileWindow>();
            Require(DesktopPlatformServices.GetWindowState(window) is PlatformResult<DesktopWindowState>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                "A Window on another host did not report the owner unavailable.");
        }

        var unopened = false;
        try { _ = DesktopPlatformServices.GetWindowState(new FileWindow()); }
        catch (InvalidOperationException) { unopened = true; }
        Require(unopened, "The state of a Window that never opened did not throw.");
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<FileViewModel>();
        services.AddScoped<Func<IBridgeTransport, FileViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddRunicPlatformServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Opens Windows without a native window, as CS-WebUI does for Runic Desktop's platform services.
    private sealed class ForeignHost(IServiceProvider services) : IRunicWindowHost
    {
        public IServiceProvider Services { get; } = services;

        public ValueTask<IBridgeWindow> OpenAsync<TViewModel>(RunicWindowOpening<TViewModel> opening, CancellationToken cancellationToken)
            where TViewModel : class => ValueTask.FromResult<IBridgeWindow>(new ForeignWindow(opening.Scope));

        public IReadOnlyList<RunicWindowDiagnostic> Validate(RunicWindowOptions options) => [];
    }

    private sealed class ForeignWindow(AsyncServiceScope scope) : IBridgeWindow
    {
        public ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new RunicWindowCloseResult(true, 0, Task.CompletedTask));

        public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }
}
