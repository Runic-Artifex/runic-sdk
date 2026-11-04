using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// B1 and B2 on Runic Desktop: a ViewModel's IRunicWindowPresentation follows its embedded window, presents it with the
// platform's presentation and the activation context, and switches its color scheme.
internal sealed class WindowPresentationChecks
{
    private static readonly DesktopWindowActivity Active = new() { IsActive = true, IsVisible = true };

    [Test, NotInParallel(Order = 44)]
    public async Task ThePresentationFollowsTheEmbeddedWindow()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory { InitialActivity = Active };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        List<DesktopActivationContext?> presented = [];
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
        })
        {
            Present = (_, context, _) =>
            {
                presented.Add(context);
                return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
            },
        };
        var window = await windows.OpenWindowAsync<PresentationWindow>();
        var presentation = window.ViewModel.Window;
        Require(ReferenceEquals(presentation, window.Presentation), "The ViewModel and the Window hold different presentations.");
        var expected = RunicWindowPresentationFeatures.Present | RunicWindowPresentationFeatures.Minimize |
            RunicWindowPresentationFeatures.Visibility | RunicWindowPresentationFeatures.ColorScheme | RunicWindowPresentationFeatures.Activity |
            (OperatingSystem.IsMacOS() ? RunicWindowPresentationFeatures.DocumentEdited : RunicWindowPresentationFeatures.None);
        Require(presentation.Features == expected, $"Features: {presentation.Features}, expected {expected}.");
        await WaitUntilAsync(() => presentation.Activity == new RunicWindowActivity(true, false, true));

        var activation = new DesktopActivationContext(activationToken: "launch");
        Require(await presentation.PresentAsync(activation) is PlatformResult<PlatformUnit>.Success, "Presenting failed.");
        Require(presented is [var context] && ReferenceEquals(context, activation), "The platform presentation did not receive the activation context.");

        Require(await presentation.SetVisibleAsync(false) is PlatformResult<PlatformUnit>.Success
            && factory.Hosts[0].Visibility.SequenceEqual([false]), "Hiding did not reach the window.");
        factory.Hosts[0].Report(new DesktopWindowActivity());
        await WaitUntilAsync(() => !presentation.IsVisible && !presentation.IsActive);
        Require(await presentation.SetFullScreenAsync(true) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "A window without full screen did not report it unavailable.");
        if (!OperatingSystem.IsMacOS())
            Require(await presentation.SetDocumentEditedAsync(true) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "A window without the edited mark did not report it unavailable.");

        var schemes = new List<string?>();
        presentation.PropertyChanged += (_, args) => { lock (schemes) schemes.Add(args.PropertyName); };
        Require(await presentation.SetColorSchemeAsync(DesktopColorScheme.Dark) is PlatformResult<PlatformUnit>.Success
            && factory.Hosts[0].Schemes.SequenceEqual([DesktopColorScheme.Dark]), "The color scheme did not reach the window.");
        Require(presentation.ColorScheme == DesktopColorScheme.Dark, "The presentation does not report the window's scheme.");
        await WaitUntilAsync(() => { lock (schemes) return schemes.Contains(nameof(IRunicWindowPresentation.ColorScheme)); });
        // The fake host's scheme belongs to the process, as GTK's does: the application's scheme reaches the presentation too.
        Require(!presentation.Features.HasFlag(RunicWindowPresentationFeatures.PerWindowColorScheme), "A shared scheme was reported per window.");
        lock (schemes) schemes.Clear();
        await desktop.SetColorSchemeAsync(DesktopColorScheme.Light);
        Require(presentation.ColorScheme == DesktopColorScheme.Light, $"The application's scheme was not reported: {presentation.ColorScheme}.");
        await WaitUntilAsync(() => { lock (schemes) return schemes.Contains(nameof(IRunicWindowPresentation.ColorScheme)); });

        await DesktopPlatformServices.GetDesktopWindow(window)!.CloseAsync();
        await window.WaitForCloseAsync();
        Require(await presentation.MinimizeAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed },
            "A closed window did not report the owner closed.");
        Require(presentation.Features == RunicWindowPresentationFeatures.None, "A closed window reports features.");
        await window.DisposeAsync();
        await WaitUntilAsync(() => presentation.Activity == RunicWindowActivity.Closed);
    }

    [Test, NotInParallel(Order = 45)]
    public async Task WithoutAPlatformProviderPresentingFocusesTheWindow()
    {
        await using var provider = CreateProvider();
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var windows = new RunicDesktopHost(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded },
        })
        {
            Present = static (_, _, _) => ValueTask.FromResult<PlatformResult<PlatformUnit>>(
                new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)),
        };
        await using var window = await windows.OpenWindowAsync<PresentationWindow>();
        var presentation = window.Presentation;
        // A host without activity counts as active and visible, and cannot change its scheme.
        Require(presentation.Activity == RunicWindowActivity.Assumed, "A window without activity is not assumed active.");
        Require(!presentation.Features.HasFlag(RunicWindowPresentationFeatures.Activity), "A window without activity reports it.");
        Require(await presentation.PresentAsync() is PlatformResult<PlatformUnit>.Success && factory.Hosts[0].Focused == 1,
            "Presenting without a platform provider did not focus the window.");
        Require(await presentation.SetColorSchemeAsync(DesktopColorScheme.Light) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "A window without color schemes did not report it unavailable.");
    }

    [Test, NotInParallel(Order = 46)]
    public void FeaturesFollowTheWindowCapabilities()
    {
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.Focus | DesktopWindowCapabilities.FullScreen, open: false, macOS: false)
            == RunicWindowPresentationFeatures.None, "A closed window reports features.");
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.None, open: true, macOS: true)
            == RunicWindowPresentationFeatures.None, "An installed browser reports features.");
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.NativeHandle | DesktopWindowCapabilities.FullScreen, open: true, macOS: true)
            == (RunicWindowPresentationFeatures.FullScreen | RunicWindowPresentationFeatures.DocumentEdited), "macOS features are wrong.");
        Require(DesktopWindowPresentation.Convert(null) == RunicWindowActivity.Assumed, "No activity is not assumed active.");
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.ColorScheme | DesktopWindowCapabilities.PerWindowColorScheme, open: true, macOS: false)
            == (RunicWindowPresentationFeatures.ColorScheme | RunicWindowPresentationFeatures.PerWindowColorScheme), "A per-window scheme was not reported.");
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.PerWindowColorScheme, open: true, macOS: false)
            == RunicWindowPresentationFeatures.None, "A per-window flag without color schemes was reported.");
        Require(DesktopWindowPresentation.FeaturesOf(DesktopWindowCapabilities.Ownership, open: true, macOS: false)
            == RunicWindowPresentationFeatures.OwnedWindows, "A window that owns windows did not report it.");
    }

    // B6: RunicWindowOptions.Owner and Modality reach the owned window's native host; an owner that closed is ignored.
    [Test, NotInParallel(Order = 46)]
    public async Task AnOwnedWindowOpensOverItsOwner()
    {
        var logs = new RecordingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddScoped<PresentationViewModel>();
        services.AddScoped<Func<IBridgeTransport, PresentationViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddRunicWindowService();
        await using var provider = services.BuildServiceProvider();
        var factory = new NativeDispatchFactory { Owns = true };
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
        var windows = TestWindows.EmbeddedHtml(provider, desktop);
        await using var owner = await windows.OpenWindowAsync<PresentationWindow>();
        Require(owner.Presentation.Features.HasFlag(RunicWindowPresentationFeatures.OwnedWindows), "The owner cannot own windows.");
        await using var owned = await windows.OpenWindowAsync<PresentationWindow>(new RunicWindowOptions
        {
            Owner = owner,
            Modality = RunicWindowModality.Window,
        });
        Require(factory.Hosts[1].Options is { OwnerHandle: NativeDispatchHost.Handle, Modality: DesktopWindowModality.Window },
            "The owned window's host did not receive its owner and modality.");
        Require(RunicDesktopHost.GetSurface(owned) is not null && ((DesktopBridgeWindow<PresentationViewModel>)owned.Host).Window.Owner?.Window ==
            ((DesktopBridgeWindow<PresentationViewModel>)owner.Host).Window, "The Desktop window does not name its owner.");
        // The owner closes: the owned window closes with it.
        await owner.CloseAsync(TimeSpan.Zero);
        await owned.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // An owner that is not open is ignored and logged.
        await using var late = await windows.OpenWindowAsync<PresentationWindow>(new RunicWindowOptions { Owner = owner });
        Require(factory.Hosts[2].Options is { OwnerHandle: 0 }, "A closed owner reached the native host.");
        Require(logs.Messages.Any(static message => message.Contains("opens without its owner", StringComparison.Ordinal)),
            "The ignored owner was not logged (event 1180).");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// Takes its Window's presentation in the constructor, before the window opens.
sealed class PresentationViewModel(IRunicWindowPresentation window) : INotifyPropertyChanged
{
    public IRunicWindowPresentation Window { get; } = window;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class PresentationWindow : RunicWindow<PresentationViewModel>;
