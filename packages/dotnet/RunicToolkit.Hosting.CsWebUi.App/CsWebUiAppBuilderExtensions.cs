using System;
using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using RunicToolkit.Desktop;
using RunicToolkit.Hosting;
using RunicToolkit.Hosting.WebUi;

namespace RunicToolkit.Hosting.CsWebUi;

/// <summary>Shared high-level configuration for one CS-WebUI frontend.</summary>
public sealed record CsWebUiAppOptions
{
    /// <summary>Creates complete native-window frontend options.</summary>
    public CsWebUiAppOptions(
        IFrontendAssetProvider assets,
        IRootSessionFactory rootSessionFactory,
        CsWebUiAdapterOptions adapter,
        BrowserHostOptions browserHost,
        BrowserWindowOptions browserWindow,
        TimeSpan? sessionCloseTimeout = null,
        TimeSpan? windowCloseTimeout = null)
    {
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        RootSessionFactory = rootSessionFactory ??
            throw new ArgumentNullException(nameof(rootSessionFactory));
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        BrowserHost = browserHost ?? throw new ArgumentNullException(nameof(browserHost));
        BrowserWindow = browserWindow ?? throw new ArgumentNullException(nameof(browserWindow));
        SessionCloseTimeout = sessionCloseTimeout ?? TimeSpan.FromSeconds(5);
        WindowCloseTimeout = windowCloseTimeout ?? TimeSpan.FromSeconds(5);
        _ = new WebUiModeOptions(
            BrowserHost,
            BrowserWindow,
            SessionCloseTimeout,
            WindowCloseTimeout);
    }

    /// <summary>Gets the immutable frontend assets.</summary>
    public IFrontendAssetProvider Assets { get; }

    /// <summary>Gets the root session activated after native-window creation.</summary>
    public IRootSessionFactory RootSessionFactory { get; }

    /// <summary>Gets native CS-WebUI adapter configuration.</summary>
    public CsWebUiAdapterOptions Adapter { get; }

    /// <summary>Gets browser-host identity.</summary>
    public BrowserHostOptions BrowserHost { get; }

    /// <summary>Gets native-window configuration.</summary>
    public BrowserWindowOptions BrowserWindow { get; }

    /// <summary>Gets the root-session close bound.</summary>
    public TimeSpan SessionCloseTimeout { get; }

    /// <summary>Gets the native-window close bound.</summary>
    public TimeSpan WindowCloseTimeout { get; }
}

/// <summary>
/// CS-WebUI-specific surface projected from the shared <see cref="WebUiAppBuilder"/>.
/// </summary>
public sealed class CsWebUiAppFrontendBuilder
{
    private readonly WebUiAppBuilder _application;
    private readonly CsWebUiAppFeature _feature;

    internal CsWebUiAppFrontendBuilder(WebUiAppBuilder application)
    {
        _application = application;
        _feature = application.GetOrAddFeature(static () => new CsWebUiAppFeature());
    }

    /// <summary>Gets the configured frontend name, if registration already occurred.</summary>
    public string? FrontendName => _feature.FrontendName;

    /// <summary>Registers one closed native-window frontend.</summary>
    public WebUiAppBuilder Use(string frontendName, CsWebUiAppOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frontendName);
        ArgumentNullException.ThrowIfNull(options);
        if (_feature.FrontendName is not null)
        {
            throw new InvalidOperationException(
                $"The WebUiApp already uses the '{_feature.FrontendName}' frontend.");
        }

        _feature.FrontendName = frontendName;
        var desktop = new CsWebUiDesktopServices(options.BrowserWindow.BrowserProfile);
        _application.Services.AddSingleton(desktop);
        _application.Services.AddSingleton<IDesktopCapabilities>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopApplicationLifetime>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopWindow>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopFocus>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopDispatcher>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopKeyboardAccelerators>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopClipboard>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopFileDialogs>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopDropTarget>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopExternalLauncher>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopNotifications>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopBrowserProfile>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopBrowserStorage>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        _application.Services.AddSingleton<IDesktopWindowManager>(
            static services => services.GetRequiredService<CsWebUiDesktopServices>());
        var stop = new ApplicationStopControllerBinding();
        var endpoint = new FrontendAssetEndpoint(
            options.Assets,
            new Uri($"app://{options.BrowserHost.ApplicationId}/"));
        _application.Application.AddValidator(
            LaunchKind.UserInterface,
            new FrontendAssetValidator(options.Assets));
        _application.Application.AddModeRunner(new WebUiModeRunner(
            new CsWebUiBrowserHostFactory(options.Adapter),
            options.RootSessionFactory,
            endpoint,
            stop,
            new WebUiModeOptions(
                options.BrowserHost,
                options.BrowserWindow,
                options.SessionCloseTimeout,
                options.WindowCloseTimeout),
            desktop));
        _application.OnBuilt(application => stop.Bind(application.StopController));
        return _application;
    }
}

internal sealed class CsWebUiAppFeature
{
    internal string? FrontendName { get; set; }
}

/// <summary>Contributes CS-WebUI members to the common high-level builder.</summary>
public static class CsWebUiAppBuilderExtensions
{
    extension(WebUiAppBuilder builder)
    {
        /// <summary>Gets the CS-WebUI-specific builder surface.</summary>
        public CsWebUiAppFrontendBuilder CsWebUi => new(builder);

        /// <summary>Registers one closed native-window frontend.</summary>
        public WebUiAppBuilder UseCsWebUi(
            string frontendName,
            CsWebUiAppOptions options) =>
            new CsWebUiAppFrontendBuilder(builder).Use(frontendName, options);
    }
}
