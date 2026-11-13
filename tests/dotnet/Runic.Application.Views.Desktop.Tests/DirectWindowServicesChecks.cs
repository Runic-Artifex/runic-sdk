using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// sdk#896: DesktopPlatformServices.AttachWindowServices gives a DI scope's per-window services a DesktopWindow the
// application opened itself, with the results of a RunicDesktopHost Window; ForWindowPresentation is its presentation for
// ViewModels without a scope.
internal sealed class DirectWindowServicesChecks
{
    private static readonly DesktopWindowActivity Active = new() { IsActive = true, IsVisible = true };

    // The same requests on a RunicDesktopHost Window and on a directly opened window give the same results.
    [Test, NotInParallel(Order = 47)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PresentMinimizeAndColorSchemeGiveTheResultsOfAHostWindow(bool direct)
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialActivity = Active };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        List<DesktopActivationContext?> presented = [];
        Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> present = (_, context, _) =>
        {
            lock (presented) presented.Add(context);
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
        };
        IRunicWindowPresentation presentation;
        DesktopWindow window;
        await using var scope = provider.CreateAsyncScope();
        IAsyncDisposable? opened = null;
        try
        {
            if (direct)
            {
                var surface = await desktop.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
                window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
                var viewModel = scope.ServiceProvider.GetRequiredService<PresentationViewModel>();
                opened = DesktopPlatformServices.AttachWindowServicesCore(window, scope.ServiceProvider, null, present);
                presentation = viewModel.Window;
                opened = new Both(opened, surface);
            }
            else
            {
                var host = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
                {
                    SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
                    WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
                }) { Present = present };
                var hostWindow = await host.OpenWindowAsync<PresentationWindow>();
                opened = hostWindow;
                presentation = hostWindow.ViewModel.Window;
                window = DesktopPlatformServices.GetDesktopWindow(hostWindow)!;
            }
            var native = factory.Hosts[0];
            var expected = RunicWindowPresentationFeatures.Present | RunicWindowPresentationFeatures.Minimize |
                RunicWindowPresentationFeatures.Visibility | RunicWindowPresentationFeatures.ColorScheme | RunicWindowPresentationFeatures.Activity |
                (OperatingSystem.IsMacOS() ? RunicWindowPresentationFeatures.DocumentEdited : RunicWindowPresentationFeatures.None);
            Require(presentation.Features == expected, $"Features: {presentation.Features}, expected {expected}.");
            await WaitUntilAsync(() => presentation.Activity == new RunicWindowActivity(true, false, true));

            var activation = new DesktopActivationContext(activationToken: "launch");
            Require(await presentation.PresentAsync(activation) is PlatformResult<PlatformUnit>.Success, "Presenting failed.");
            lock (presented) Require(presented is [var context] && ReferenceEquals(context, activation), "The platform presentation did not receive the activation context.");
            Require(await presentation.MinimizeAsync() is PlatformResult<PlatformUnit>.Success, "Minimizing failed.");
            Require(await presentation.SetFullScreenAsync(true) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "A window without full screen did not report it unavailable.");

            var changes = new List<string?>();
            presentation.PropertyChanged += (_, args) => { lock (changes) changes.Add(args.PropertyName); };
            Require(await presentation.SetColorSchemeAsync(DesktopColorScheme.Dark) is PlatformResult<PlatformUnit>.Success
                && native.Schemes.SequenceEqual([DesktopColorScheme.Dark]), "The color scheme did not reach the window.");
            Require(presentation.ColorScheme == DesktopColorScheme.Dark, "The presentation does not report the window's scheme.");
            await WaitUntilAsync(() => { lock (changes) return changes.Contains(nameof(IRunicWindowPresentation.ColorScheme)); });

            native.Report(new DesktopWindowActivity { IsMinimized = true });
            await WaitUntilAsync(() => presentation.IsMinimized && !presentation.IsActive);

            await window.CloseAsync();
            await WaitUntilAsync(() => presentation.Activity == RunicWindowActivity.Closed || !window.IsOpen);
            Require(await presentation.MinimizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
                "A closed window did not report the owner closed.");
            Require(presentation.Features == RunicWindowPresentationFeatures.None, "A closed window reports features.");
        }
        finally
        {
            if (opened is not null) await opened.DisposeAsync();
        }
        await WaitUntilAsync(() => presentation.Activity == RunicWindowActivity.Closed);
    }

    // A ViewModel the scope resolved before the attach holds the object that is attached; one resolved after gets it too.
    [Test, NotInParallel(Order = 47)]
    public async Task TheScopesPresentationIsAttachedBeforeAndAfterTheViewModelIsResolved()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialActivity = Active };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var direct = await DirectWindow.OpenAsync(desktop, provider);
        var before = direct.Scope.ServiceProvider.GetRequiredService<PresentationViewModel>();
        Require(await before.Window.PresentAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerUnavailable },
            "An unattached scope's presentation did not report the owner unavailable.");
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        var after = direct.Scope.ServiceProvider.GetRequiredService<IRunicWindowPresentation>();
        Require(ReferenceEquals(before.Window, after) && ReferenceEquals(services.Presentation, after),
            "The ViewModel, the scope and the binding hold different presentations.");
        Require(ReferenceEquals(services.Window, direct.Window) && ReferenceEquals(services.NativeOwner.Window, direct.Window),
            "The binding names another window.");
        Require(await before.Window.PresentAsync() is PlatformResult<PlatformUnit>.Success, "The attached presentation did not present the window.");
        Require(direct.Scope.ServiceProvider.GetRequiredService<IRunicWindowContext>().Window is null,
            "A direct window's context reports an IRunicWindow.");

        // ForWindowPresentation is the same object for the window on each call, and not the scope's.
        var shared = DesktopPlatformServices.ForWindowPresentation(direct.Window);
        Require(ReferenceEquals(shared, DesktopPlatformServices.ForWindowPresentation(direct.Window)) && !ReferenceEquals(shared, after),
            "ForWindowPresentation did not return one presentation per window.");
        Require(shared.Features == after.Features, $"ForWindowPresentation's features {shared.Features} differ from {after.Features}.");
        Require(await shared.SetColorSchemeAsync(DesktopColorScheme.Light) is PlatformResult<PlatformUnit>.Success
            && factory.Hosts[0].Schemes.Last() == DesktopColorScheme.Light, "ForWindowPresentation did not set the scheme.");

        // Disposing detaches early; the window and the scope stay.
        await services.DisposeAsync();
        Require(direct.Window.IsOpen, "Disposing the binding closed the window.");
        Require(await after.MinimizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A detached presentation did not report the owner closed.");
        await WaitUntilAsync(() => after.Activity == RunicWindowActivity.Closed);
        Require(shared.Features != RunicWindowPresentationFeatures.None, "Detaching the scope closed ForWindowPresentation's presentation.");
        await direct.Window.CloseAsync();
        await WaitUntilAsync(() => shared.Activity == RunicWindowActivity.Closed);
    }

    // IRunicWindowLocale follows and sets the session's locale; the native direction and the surface's page language follow.
    [Test, NotInParallel(Order = 47)]
    public async Task TheLocaleAndTheDirectionFollowTheSession()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var direct = await DirectWindow.OpenAsync(desktop, provider);
        var locale = direct.Scope.ServiceProvider.GetRequiredService<IRunicWindowLocale>();
        locale.Locale = "de";
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        Require(direct.Content.Locale == "de", $"The session did not take the window's locale: {direct.Content.Locale}.");
        Require(direct.Surface.PageLanguage == "de", $"The surface's pages are in {direct.Surface.PageLanguage}.");
        await WaitUntilAsync(() => factory.Hosts[0].Directions.Contains(DesktopLayoutDirection.LeftToRight));

        var changed = 0;
        locale.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(IRunicWindowLocale.Locale)) Interlocked.Increment(ref changed); };
        locale.Locale = "he";
        Require(direct.Content.Locale == "he" && Volatile.Read(ref changed) > 0, "Setting the window's locale did not reach the session.");
        await WaitUntilAsync(() => direct.Window.LayoutDirection == DesktopLayoutDirection.RightToLeft);
        Require(direct.Surface.PageLanguage == "he", "The page language did not follow.");
        // A locale set on the session directly is the window's own.
        direct.Content.Locale = "en";
        Require(locale.Locale == "en" && locale.OwnLocale == "en", $"The window's locale did not follow the session: {locale.Locale}.");
        await WaitUntilAsync(() => direct.Window.LayoutDirection == DesktopLayoutDirection.LeftToRight);

        // After the binding ends, the session's locale no longer reaches the window.
        await services.DisposeAsync();
        var directions = factory.Hosts[0].Directions.Count;
        direct.Content.Locale = "ar";
        await Task.Delay(50);
        Require(factory.Hosts[0].Directions.Count == directions && direct.Surface.PageLanguage == "en",
            "A detached window still followed the session's locale.");
    }

    // A direction the application gave the window that is not its locale's stays.
    [Test, NotInParallel(Order = 47)]
    public async Task AFixedDirectionStays()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var direct = await DirectWindow.OpenAsync(desktop, provider, new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            LayoutDirection = DesktopLayoutDirection.LeftToRight,
        });
        direct.Content.Locale = "ar";
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        direct.Content.Locale = "he";
        await Task.Delay(50);
        Require(factory.Hosts[0].Directions.IsEmpty && direct.Window.LayoutDirection == DesktopLayoutDirection.LeftToRight,
            "The application's own direction was replaced.");
    }

    // appearance/1 reaches the page of a window only this computer can reach.
    [Test, NotInParallel(Order = 47)]
    [Arguments(DesktopNetworkExposure.Loopback)]
    [Arguments(DesktopNetworkExposure.AllInterfaces)]
    public async Task TheAppearanceIsPushedOnlyToALocalWindow(DesktopNetworkExposure exposure)
    {
        var settings = new HostAppearanceSettings();
        await using var provider = CreateProvider(services => services.Replace(ServiceDescriptor.Singleton<IDesktopSettings>(settings)));
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            WaitForConnection = false,
            NetworkExposure = exposure,
            Security = DesktopSecurityPolicy.Default,
        });
        await using var direct = await DirectWindow.OpenAsync(desktop, provider);
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        var dark = new DesktopAppearance(DesktopColorScheme.Dark) { TextScale = 1.25 };
        settings.Set(dark);
        if (exposure == DesktopNetworkExposure.Loopback)
        {
            await WaitUntilAsync(() => direct.Content.Appearance == dark);
            await direct.Window.CloseAsync();
            await Task.Delay(50);
            settings.Set(new DesktopAppearance(DesktopColorScheme.Light));
            await Task.Delay(50);
            Require(direct.Content.Appearance == dark, "A closed window kept following the appearance.");
        }
        else
        {
            await Task.Delay(100);
            Require(direct.Content.Appearance is null, $"A window on every interface received {direct.Content.Appearance}.");
        }
    }

    // IRunicConnections lists the page's connection; the platform services are bound to the window's owner.
    [Test, NotInParallel(Order = 47)]
    public async Task TheConnectionsAndThePlatformServicesFollowTheWindow()
    {
        await using var provider = CreateProvider(static services => services.AddRunicPlatformServices());
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var direct = await DirectWindow.OpenAsync(desktop, provider);
        var connections = direct.Scope.ServiceProvider.GetRequiredService<IRunicConnections>();
        var platform = direct.Scope.ServiceProvider.GetRequiredService<DesktopWindowPlatformServices>();
        Require(!platform.IsBound, "The platform services were bound before the attach.");
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        Require(platform.IsBound, "The platform services were not bound to the window.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var page = await DesktopWireClient.ConnectAsync(direct.Surface, cancellation.Token);
        await page.HelloAsync(cancellation.Token);
        await WaitUntilAsync(() => connections.Current.Count == 1);

        await direct.Window.CloseAsync();
        await WaitUntilAsync(() => platform.GetSnapshot().Statuses.Values
            .All(static status => status is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)));
        Require(await platform.Clipboard.WriteTextAsync("text") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The clipboard of a closed window did not report the owner closed.");
    }

    // IRunicWindowContext.RequestCloseAsync asks the window's ConfirmCloseAsync, as its close button does.
    [Test, NotInParallel(Order = 47)]
    public async Task RequestCloseAsksTheWindowsConfirmation()
    {
        await using var provider = CreateProvider();
        var factory = new ConfirmingWindowFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var asked = 0;
        var allow = false;
        await using var direct = await DirectWindow.OpenAsync(desktop, provider, new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            ConfirmCloseAsync = _ =>
            {
                Interlocked.Increment(ref asked);
                return ValueTask.FromResult(Volatile.Read(ref allow));
            },
        });
        var context = direct.Scope.ServiceProvider.GetRequiredService<IRunicWindowContext>();
        await RequireThrowsAsync<InvalidOperationException>(() => context.RequestCloseAsync().AsTask(), "an unattached context");
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        Require(!await context.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) && Volatile.Read(ref asked) == 1 && direct.Window.IsOpen,
            "A refused request closed the window, or did not ask ConfirmCloseAsync.");
        Volatile.Write(ref allow, true);
        Require(await context.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)) && Volatile.Read(ref asked) == 2,
            "An allowed request did not close the window.");
        await direct.Window.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => direct.Scope.ServiceProvider.GetRequiredService<IRunicWindowPresentation>().Activity == RunicWindowActivity.Closed);
        await RequireThrowsAsync<InvalidOperationException>(() => context.RequestCloseAsync().AsTask(), "a context whose window closed");
    }

    // The window no longer holds the binding or the scope once it closed.
    [Test, NotInParallel(Order = 47)]
    public async Task AClosedWindowReleasesTheBinding()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialActivity = Active, Directs = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var surface = await desktop.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
        await using var _ = surface;
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var (binding, context) = await AttachAndReleaseAsync(window, surface, provider);
        Require(binding.IsAlive, "The binding was collected while its window was open.");
        await window.CloseAsync();
        for (var waited = 0; binding.IsAlive || context.IsAlive; waited += 20)
        {
            if (waited > 5000) throw new InvalidOperationException("The closed window still holds the binding or the scope.");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20);
        }
        GC.KeepAlive(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Binding, WeakReference Context)> AttachAndReleaseAsync(DesktopWindow window, DesktopSurface surface,
        ServiceProvider provider)
    {
        var scope = provider.CreateAsyncScope();
        var transport = new DesktopBridgeTransport(surface);
        var content = new WindowContentSession(transport, new WindowContentSessionOptions());
        var services = DesktopPlatformServices.AttachWindowServicesCore(window, scope.ServiceProvider, content, Presented);
        var context = scope.ServiceProvider.GetRequiredService<IRunicWindowContext>();
        _ = scope.ServiceProvider.GetRequiredService<IRunicWindowPresentation>().Features;
        content.Dispose();
        transport.Dispose();
        await scope.DisposeAsync();
        return (new WeakReference(services), new WeakReference(context));
    }

    [Test, NotInParallel(Order = 47)]
    public async Task TheRefusals()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        await using var direct = await DirectWindow.OpenAsync(desktop, provider);
        await using var other = await DirectWindow.OpenAsync(desktop, provider);
        // A session of another surface.
        RequireThrows<ArgumentException>(() => DesktopPlatformServices.AttachWindowServices(direct.Window, direct.Scope.ServiceProvider, other.Content),
            "a session of another surface");
        // Attached twice; the first binding stays.
        await using var services = DesktopPlatformServices.AttachWindowServicesCore(direct.Window, direct.Scope.ServiceProvider, direct.Content, Presented);
        RequireThrows<InvalidOperationException>(() => DesktopPlatformServices.AttachWindowServices(direct.Window, direct.Scope.ServiceProvider, direct.Content),
            "a scope attached twice");
        Require(await services.Presentation.PresentAsync() is PlatformResult<PlatformUnit>.Success, "A refused second attach detached the first.");
        // A scope of a Window that OpenWindowAsync opened.
        await using var hostWindow = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<PresentationWindow>();
        RequireThrows<InvalidOperationException>(() => DesktopPlatformServices.AttachWindowServices(other.Window, hostWindow.Services, other.Content),
            "the scope of a host Window");
        // A closed window.
        await other.Window.CloseAsync();
        await using var fresh = provider.CreateAsyncScope();
        RequireThrows<InvalidOperationException>(() => DesktopPlatformServices.AttachWindowServices(other.Window, fresh.ServiceProvider),
            "a closed window");
        RequireThrows<ArgumentNullException>(() => DesktopPlatformServices.AttachWindowServices(null!, fresh.ServiceProvider), "no window");

        // A scope without AddRunicWindowService still presents the window through the binding.
        await using var plain = new ServiceCollection().BuildServiceProvider();
        await using var third = await DirectWindow.OpenAsync(desktop, provider);
        await using var bare = DesktopPlatformServices.AttachWindowServicesCore(third.Window, plain, null, Presented);
        Require(await bare.Presentation.PresentAsync() is PlatformResult<PlatformUnit>.Success, "A scope without window services did not present.");
    }

    private static readonly Func<DesktopNativeOwner, DesktopActivationContext?, CancellationToken, ValueTask<PlatformResult<PlatformUnit>>> Presented =
        static (_, _, _) => ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));

    private static ServiceProvider CreateProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<PresentationViewModel>();
        services.AddScoped<Func<IBridgeTransport, PresentationViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddRunicWindowService();
        // The machine's own desktop settings never reach the tests.
        services.AddSingleton<IDesktopSettings>(new HostAppearanceSettings());
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // A window the application opened itself: its surface, window, scope, transport and content session.
    private sealed class DirectWindow : IAsyncDisposable
    {
        public required DesktopSurface Surface { get; init; }
        public required DesktopWindow Window { get; init; }
        public required AsyncServiceScope Scope { get; init; }
        public required DesktopBridgeTransport Transport { get; init; }
        public required WindowContentSession Content { get; init; }

        public static async Task<DirectWindow> OpenAsync(DesktopHost desktop, IServiceProvider provider, DesktopWindowOptions? options = null)
        {
            var surface = await desktop.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
            var window = await surface.OpenWindowAsync(options ?? new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            var transport = new DesktopBridgeTransport(surface);
            return new DirectWindow
            {
                Surface = surface,
                Window = window,
                Scope = provider.CreateAsyncScope(),
                Transport = transport,
                Content = new WindowContentSession(transport, new WindowContentSessionOptions()),
            };
        }

        public async ValueTask DisposeAsync()
        {
            Content.Dispose();
            Transport.Dispose();
            await Scope.DisposeAsync();
            await Surface.DisposeAsync();
        }
    }

    private sealed class Both(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var waited = 0; !condition(); waited += 10)
        {
            if (waited > 5000) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static void RequireThrows<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"No {typeof(TException).Name} for {what}.");
    }

    private static async Task RequireThrowsAsync<TException>(Func<Task> action, string what) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"No {typeof(TException).Name} for {what}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
