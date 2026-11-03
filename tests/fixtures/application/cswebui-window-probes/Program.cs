using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsWebUi;
using CsWebUiWindowProbes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

// Each probe prints one marker that CI matches. No probe opens a browser window: the window probes serve their
// content without one, through the same switch test harnesses use, and the show-failure probe selects a browser
// that does not exist or exits at once.
Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
switch (args.SingleOrDefault())
{
    case "--probe-factory-failure":
        await ProbeFactoryFailureAsync();
        break;
    case "--probe-window-close":
        return ProbeWindowClose();
    case "--probe-close-guard":
        return ProbeCloseGuard();
    case "--probe-missing-bridge":
        await ProbeMissingBridgeAsync();
        break;
    case "--probe-show-failure":
        return ProbeShowFailure();
    case "--probe-platform-services":
        await ProbePlatformServicesAsync();
        break;
    case "--probe-menu-bar":
        return ProbeMenuBar();
    case "--probe-embedded-assets":
        await ProbeEmbeddedAssetsAsync();
        break;
    case "--probe-blobs":
        return ProbeBlobs();
    case "--probe-presentation":
        return await ProbePresentationAsync();
    default:
        Console.Error.WriteLine("Usage: CsWebUiWindowProbes --probe-factory-failure | --probe-window-close | --probe-close-guard | --probe-missing-bridge | --probe-show-failure | --probe-platform-services | --probe-menu-bar | --probe-embedded-assets | --probe-blobs | --probe-presentation");
        return 2;
}
WebUiApplication.Clean();
return 0;

// A ViewModel construction failure must release the Window scope exactly once, before any native window exists.
static async Task ProbeFactoryFailureAsync()
{
    var services = new ServiceCollection();
    var scopeDisposals = 0;
    services.AddScoped(_ => new ScopeProbe(() => scopeDisposals++));
    services.AddScoped<ProbeViewModel>(provider =>
    {
        _ = provider.GetRequiredService<ScopeProbe>();
        throw new InvalidOperationException("Expected Window construction failure.");
    });
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var host = new RunicCsWebUiHost(provider);
    try
    {
        await using var window = await host.OpenWindowAsync<ProbeWindow>();
        throw new InvalidOperationException("The Window unexpectedly opened.");
    }
    catch (InvalidOperationException error) when (error.Message == "Expected Window construction failure.")
    {
        if (scopeDisposals != 1)
            throw new InvalidOperationException($"Failed Window construction released the scope {scopeDisposals} times.");
        Console.WriteLine("FIRST_WINDOW_FACTORY_FAILURE_OK");
    }
}

// AddRunicPlatformServices registers every service Runic Desktop's does, so a ViewModel resolves on both hosts.
// Without a native owner the window services report OwnerUnavailable; without a provider the application services
// report ProviderNotConfigured, and a provider the application registers first is kept.
static async Task ProbePlatformServicesAsync()
{
    var custom = Runic.Platform.DesktopInhibition.Unavailable(Runic.Platform.PlatformUnavailableReason.BackendUnavailable);
    var services = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices();
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    await using var scope = provider.CreateAsyncScope();
    var window = scope.ServiceProvider;
    const Runic.Platform.PlatformUnavailableReason noOwner = Runic.Platform.PlatformUnavailableReason.OwnerUnavailable;
    const Runic.Platform.PlatformUnavailableReason noProvider = Runic.Platform.PlatformUnavailableReason.ProviderNotConfigured;
    var platform = window.GetRequiredService<Runic.Platform.IWindowPlatformServices>();
    if (!platform.GetSnapshot().Statuses.Values.All(static status => status is Runic.Platform.CapabilityStatus.Unavailable(noOwner))
        || await window.GetRequiredService<Runic.Platform.IFileDialogs>().OpenFileAsync(new()) is not Runic.Platform.PickerResult<Runic.Platform.IReadFileLease>.Unavailable(noOwner)
        || await window.GetRequiredService<Runic.Platform.ITextClipboard>().ReadTextAsync(16) is not Runic.Platform.PlatformResult<string?>.Unavailable(noOwner)
        || await window.GetRequiredService<Runic.Platform.IDesktopFileLauncher>().LaunchAsync(Environment.CurrentDirectory, Runic.Platform.DesktopFileOperation.Reveal)
            is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noOwner)
        || await platform.AnnounceAsync("Saved") is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noOwner))
        throw new InvalidOperationException("A CS-WebUI window service did not report the missing native owner.");
    if (await provider.GetRequiredService<Runic.Platform.IDesktopSettings>().ReadAsync() is not Runic.Platform.PlatformResult<Runic.Platform.DesktopAppearance>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopNotifications>().RequestPermissionAsync() is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopStatusIcon>().HideAsync() is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopLoginStartup>().IsEnabledAsync() is not Runic.Platform.PlatformResult<bool>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopVolumes>().ListAsync() is not Runic.Platform.PlatformResult<System.Collections.Immutable.ImmutableArray<Runic.Platform.DesktopVolume>>.Unavailable(noProvider))
        throw new InvalidOperationException("A CS-WebUI application service did not report the provider unconfigured.");
    if (!ReferenceEquals(provider.GetRequiredService<Runic.Platform.IDesktopInhibition>(), custom)
        || !ReferenceEquals(window.GetRequiredService<Runic.Platform.IDesktopInhibition>(), custom))
        throw new InvalidOperationException("AddRunicPlatformServices replaced the application's inhibition.");
    var unconfigured = new ServiceCollection().AddRunicPlatformServices().BuildServiceProvider();
    if (await unconfigured.GetRequiredService<Runic.Platform.IDesktopInhibition>().AcquireAsync(Runic.Platform.DesktopInhibitionEffects.SystemSleep, "Probe")
        is not Runic.Platform.PlatformResult<Runic.Platform.IDesktopInhibitionLease>.Unavailable(noProvider))
        throw new InvalidOperationException("CS-WebUI inhibition did not report the provider unconfigured.");
    await unconfigured.DisposeAsync();
    Console.WriteLine("CSWEBUI_PLATFORM_SERVICES_OK");
}

// An idle Window opened through the application entry point closes without remaining operations, its
// WaitForCloseAsync returns, and Run returns the application's result once the Window is released.
static int ProbeWindowClose()
{
    var services = new ServiceCollection();
    services.AddScoped<ProbeViewModel>();
    services.AddRunicViews();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var result = RunicCsWebUiHost.Run(provider, async host =>
    {
        await using var window = await host.OpenWindowAsync<ProbeWindow>();
        if (window.ViewModel is null || !ReferenceEquals(window.DataContext, window.ViewModel))
            throw new InvalidOperationException("The Window was not given its ViewModel.");
        if (!ReferenceEquals(CsWebUiPlatformServices.GetWebUiWindow(window), ((CsWebUiBridgeWindow<ProbeViewModel>)window.Host).Window))
            throw new InvalidOperationException("GetWebUiWindow did not return the Window's WebUI window.");
        var close = await window.CloseAsync(TimeSpan.Zero);
        await close.Completion;
        if (!close.Drained || close.RemainingOperations != 0)
            throw new InvalidOperationException("An idle Window did not close cleanly.");
        await window.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return 7;
    });
    if (result != 7) throw new InvalidOperationException($"Run returned {result} instead of the application's result.");
    Console.WriteLine("FIRST_WINDOW_CLOSE_OK");
    return 0;
}

// B1: IRunicWindowPresentation on CS-WebUI. A serve-only Window has no window, so it reports the owner unavailable and
// counts as active; once released it reports the owner closed. CsWebUiWindowPresentation itself is checked through its
// seams: it presents a shown window, minimizes only a WebView window, reports no activity and reports a closed one.
static async Task<int> ProbePresentationAsync()
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    var services = new ServiceCollection();
    services.AddScoped<ProbeViewModel>();
    services.AddRunicViews();
    services.AddRunicWindowService();
    using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
    {
        var result = RunicCsWebUiHost.Run(provider, async host =>
        {
            var window = await host.OpenWindowAsync<ProbeWindow>();
            var presentation = window.Services.GetRequiredService<IRunicWindowPresentation>();
            Check(ReferenceEquals(presentation, window.Presentation), "The scope and the Window hold different presentations.");
            Check(presentation.Features == RunicWindowPresentationFeatures.None, $"A serve-only Window reported {presentation.Features}.");
            Check(presentation.Activity == RunicWindowActivity.Assumed, "A serve-only Window does not count as active.");
            Check(await presentation.PresentAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable
                { Reason: Runic.Platform.PlatformUnavailableReason.OwnerUnavailable }, "A serve-only Window was presented.");
            await window.DisposeAsync();
            Check(await presentation.MinimizeAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable
                { Reason: Runic.Platform.PlatformUnavailableReason.OwnerClosed }, "A released Window did not report the owner closed.");
            Check(presentation.Activity == RunicWindowActivity.Closed, $"A released Window reported {presentation.Activity}.");
            return 7;
        });
        Check(result == 7, $"Run returned {result}.");
    }

    using var native = new WebUiWindow();
    var shown = true;
    int focused = 0, minimized = 0;
    var browser = new CsWebUiWindowPresentation(native, webView: false)
    {
        IsShown = () => shown, Focus = () => focused++, Minimize = () => minimized++,
    };
    Check(browser.Features == RunicWindowPresentationFeatures.Present, $"A browser window reported {browser.Features}.");
    Check(browser.Activity == RunicWindowActivity.Assumed && browser.IsActive && browser.IsVisible, "A browser window does not count as active.");
    Check(await browser.PresentAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Success && focused == 1,
        "Presenting did not focus the WebUI window.");
    Check(await browser.MinimizeAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable
        { Reason: Runic.Platform.PlatformUnavailableReason.BackendUnavailable } && minimized == 0, "A browser window was minimized.");
    foreach (var request in new Func<ValueTask<Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>>>[]
             {
                 () => browser.SetVisibleAsync(false), () => browser.SetFullScreenAsync(true), () => browser.SetDocumentEditedAsync(true),
                 () => browser.SetColorSchemeAsync(Runic.Platform.DesktopColorScheme.Dark),
             })
        Check(await request() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable
            { Reason: Runic.Platform.PlatformUnavailableReason.BackendUnavailable }, "WebUI reported a request it cannot do.");
    var rejected = false;
    try { await browser.SetColorSchemeAsync((Runic.Platform.DesktopColorScheme)9); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "An undefined color scheme was accepted.");

    var webView = new CsWebUiWindowPresentation(native, webView: true)
    {
        IsShown = () => shown, Focus = () => throw new ObjectDisposedException("window"), Minimize = () => minimized++,
    };
    Check(webView.Features == (RunicWindowPresentationFeatures.Present | RunicWindowPresentationFeatures.Minimize),
        $"A WebView window reported {webView.Features}.");
    Check(await webView.MinimizeAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Success && minimized == 1,
        "A WebView window was not minimized.");
    Check(await webView.PresentAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable
        { Reason: Runic.Platform.PlatformUnavailableReason.OwnerClosed }, "A disposed WebUI window did not report the owner closed.");
    shown = false;
    Check(webView.Features == RunicWindowPresentationFeatures.None, "A closed WebUI window reported features.");
    Check(await browser.PresentAsync() is Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable
        { Reason: Runic.Platform.PlatformUnavailableReason.OwnerClosed } && focused == 1, "A closed WebUI window was presented.");
    Console.WriteLine("CSWEBUI_PRESENTATION_OK");
    return 0;
}

// Without AddRunicViews(), validation and OpenWindowAsync name the missing Bridge before a native window
// or ViewModel exists, also for a container that cannot report its registrations.
static async Task ProbeMissingBridgeAsync()
{
    var services = new ServiceCollection();
    var viewModels = 0;
    services.AddScoped(_ =>
    {
        viewModels++;
        return new ProbeViewModel();
    });
    var log = new RecordingLoggerFactory();
    services.AddSingleton<ILoggerFactory>(log);
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var validation = new RunicCsWebUiHost(provider).ValidateWindow<ProbeWindow>();
    if (validation.IsValid || validation.Errors.Count != 1)
        throw new InvalidOperationException("ValidateWindow did not report the missing Bridge.");
    RequireBridgeNotRegistered(new RunicWindowConfigurationException(validation.Errors), "ValidateWindow");
    foreach (var (name, candidate) in new (string, IServiceProvider)[]
        { ("OpenWindowAsync", provider), ("OpenWindowAsync without IServiceProviderIsService", new OpaqueProvider(provider)) })
    {
        try
        {
            await using var window = await new RunicCsWebUiHost(candidate).OpenWindowAsync<ProbeWindow>();
            throw new InvalidOperationException($"{name} opened a Window without its Bridge.");
        }
        catch (RunicWindowConfigurationException error)
        {
            RequireBridgeNotRegistered(error, name);
        }
    }
    if (viewModels != 0)
        throw new InvalidOperationException($"A missing Bridge still constructed {viewModels} ViewModels.");
    if (log.Entries.Count != 3 || log.Entries.Any(static entry => entry.Category != "Runic.Application.Views" ||
            entry.EventId.Id != 1050 || entry.EventId.Name != "WindowRegistrationMissing" ||
            entry.Level != LogLevel.Error || !entry.Message.Contains("bridge-not-registered", StringComparison.Ordinal)))
        throw new InvalidOperationException("A missing Bridge was not logged once per check as event 1050 in Runic.Application.Views.");

    var registered = new ServiceCollection();
    registered.AddScoped<ProbeViewModel>();
    registered.AddRunicViews();
    using var complete = registered.BuildServiceProvider();
    new RunicCsWebUiHost(complete).ValidateWindow<ProbeWindow>().ThrowIfInvalid();
    Console.WriteLine("FIRST_WINDOW_MISSING_BRIDGE_OK");
}

// CS-WebUI cannot keep a browser window open when the user closes it: a Window with GuardClose opens unguarded and
// logs WindowCloseGuardUnavailable once. RequestCloseAsync, the application asking, is still guarded.
// Also: a WebUI library that the application selects, here the bundled one, is logged once as event 2101, because
// Runic cannot check its version (SEC-23).
static int ProbeCloseGuard()
{
    var nativeLibrary = BundledNativeLibrary();
    Environment.SetEnvironmentVariable("CSWEBUI_NATIVE_LIBRARY", nativeLibrary);
    var services = new ServiceCollection();
    services.AddScoped<GuardedProbeViewModel>();
    services.AddRunicViews();
    var log = new RecordingLoggerFactory();
    services.AddSingleton<ILoggerFactory>(log);
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var result = RunicCsWebUiHost.Run(provider, async host =>
    {
        await using var window = await host.OpenWindowAsync<GuardedProbeWindow>();
        window.Answer = false;
        if (await window.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) || window.Asked != 1)
            throw new InvalidOperationException("RequestCloseAsync on CS-WebUI did not keep the window open when CanCloseAsync refused.");
        window.Answer = true;
        if (!await window.RequestCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) || window.Asked != 2)
            throw new InvalidOperationException("RequestCloseAsync on CS-WebUI did not close the window when CanCloseAsync allowed it.");
        await window.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return 7;
    });
    if (result != 7) throw new InvalidOperationException($"Run returned {result} instead of the application's result.");
    LogEntry[] entries;
    lock (log.Entries) entries = [.. log.Entries];
    if (entries.Count(static entry => entry.EventId.Id == 1053) != 1 || !entries.Any(static entry =>
            entry is { Category: "Runic.Application.Views", Level: LogLevel.Warning } &&
            entry.EventId is { Id: 1053, Name: "WindowCloseGuardUnavailable" } &&
            entry.Message.Contains(nameof(GuardedProbeWindow), StringComparison.Ordinal) &&
            entry.Message.Contains("CS-WebUI", StringComparison.Ordinal)))
        throw new InvalidOperationException("A guarded Window on CS-WebUI was not logged once as event 1053:\n" +
            string.Join('\n', entries.Select(static entry => entry.ToString())));
    if (entries.Count(static entry => entry.EventId.Id == 2101) != 1 || !entries.Any(entry =>
            entry is { Category: "Runic.Application.Views.CsWebUi", Level: LogLevel.Warning } &&
            entry.EventId is { Id: 2101, Name: "CsWebUiNativeLibraryUnchecked" } &&
            entry.Message.Contains(nativeLibrary, StringComparison.Ordinal) &&
            entry.Message.Contains("efac0e24", StringComparison.Ordinal) &&
            entry.Message.Contains("2b35db13f", StringComparison.Ordinal)))
        throw new InvalidOperationException("The WebUI library from CSWEBUI_NATIVE_LIBRARY was not logged once as event 2101:\n" +
            string.Join('\n', entries.Select(static entry => entry.ToString())));
    Console.WriteLine("FIRST_WINDOW_CLOSE_GUARD_OK");
    return 0;
}

// A browser that cannot show the window fails OpenWindowAsync with a Window diagnostic that keeps WebUI's exception,
// logs event 2100 once, and makes Run return 1 when the application does not handle it, instead of an unhandled
// exception (#265). The browser comes from CSWEBUI_BROWSER_PATH: one that does not exist on every platform, and on
// Linux a script named chromium that exits at once, so its page never connects.
static int ProbeShowFailure()
{
    Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
    var folder = Directory.CreateTempSubdirectory("runic-cswebui-show-");
    try
    {
        var missing = Path.Combine(folder.FullName, "missing", "chromium");
        RequireShowFailure(missing, (diagnostic, error) =>
            diagnostic.Code == "window-show-failed" && error.Message.Contains("CSWEBUI_BROWSER_PATH", StringComparison.Ordinal));
        if (OperatingSystem.IsLinux())
        {
            var browser = Path.Combine(folder.FullName, "chromium");
            File.WriteAllText(browser, "#!/bin/sh\necho 'cannot render' >&2\nexit 1\n");
            File.SetUnixFileMode(browser, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            RequireShowFailure(browser, (diagnostic, error) =>
                diagnostic.Code is "browser-not-connected" or "browser-not-started" &&
                error is WebUiShowException { Browser: WebUiBrowser.Chromium } show && show.BrowserPath == browser);
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable, null);
        folder.Delete(recursive: true);
    }
    Console.WriteLine("FIRST_WINDOW_SHOW_FAILURE_OK");
    return 0;
}

// The in-page menu bar (D036.14): the host serves its bar on the Window's reserved routes, and menu-bar-probe.mjs drives
// them from Chromium (the state reaches the page, enabled follows CanExecuteChanged, invoke answers executed, disabled
// and stale, SetMenuBar reaches the page, the shortcut lease). The Window serves a page that loads only webui.js and
// runic-cswebui.js; the probe prints its URL, waits for a line on standard input and then checks what the commands ran.
static int ProbeMenuBar()
{
    if (new RunicCsWebUiHostOptions().MenuBar.Count != 0) throw new InvalidOperationException("RunicCsWebUiHostOptions.MenuBar is not empty by default.");
    try
    {
        _ = new RunicCsWebUiHost(new ServiceCollection().BuildServiceProvider(), new RunicCsWebUiHostOptions
        {
            MenuBar = [DesktopMenuProbe.Menu("File", new Runic.Platform.DesktopMenuItem("Save", "runic.save"))],
        });
        throw new InvalidOperationException("A reserved command id did not fail the host.");
    }
    catch (ArgumentException error) when (error.Message.Contains("runic.", StringComparison.Ordinal))
    {
    }
    var root = Directory.CreateTempSubdirectory("runic-cswebui-menu-");
    try
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "www", "runic-cswebui.js"), Path.Combine(root.FullName, "runic-cswebui.js"));
        File.WriteAllText(Path.Combine(root.FullName, "index.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Menu bar probe</title>" +
            "<script src=\"/webui.js\"></script><script src=\"runic-cswebui.js\"></script></head><body></body></html>");
        var services = new ServiceCollection();
        services.AddSingleton<MenuProbe>();
        services.AddScoped<MenuProbeViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var probe = provider.GetRequiredService<MenuProbe>();
        var result = RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions { MenuBar = MenuProbe.Bar("a") }, async host =>
        {
            probe.Host = host;
            if (host.MenuBar.Count != 3) throw new InvalidOperationException("RunicCsWebUiHost.MenuBar is not the declared bar.");
            await using var window = await host.OpenWindowAsync<MenuProbeWindow>(new RunicWindowOptions { ContentRoot = root.FullName });
            await window.WaitForCloseAsync();
            return 0;
        });
        if (result != 0) return result;
        if (probe.Saves != 1 || probe.Locks != 1 || !probe.Modes.SequenceEqual(["b", "a"]) || probe.Undos != 0)
            throw new InvalidOperationException(
                $"The menu commands ran {probe.Saves} saves, {probe.Locks} locks, {probe.Undos} undos and modes [{string.Join(", ", probe.Modes)}].");
        Console.WriteLine("CSWEBUI_MENU_BAR_OK");
        return 0;
    }
    finally
    {
        root.Delete(recursive: true);
    }
}

// blobs/1 on CS-WebUI (blobs-probe.mjs drives the page from Chromium): the window's blob endpoint serves the avatar to
// the page's origin, takes a streamed upload with the connection's token, and the attach command reads it.
static int ProbeBlobs()
{
    var root = Directory.CreateTempSubdirectory("runic-cswebui-blobs-");
    try
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "www", "runic-cswebui.js"), Path.Combine(root.FullName, "runic-cswebui.js"));
        File.WriteAllText(Path.Combine(root.FullName, "index.html"),
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Blob probe</title>" +
            "<script src=\"/webui.js\"></script><script src=\"runic-cswebui.js\"></script></head><body></body></html>");
        var services = new ServiceCollection();
        services.AddScoped<BlobProbeViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        BlobProbeViewModel? model = null;
        var result = RunicCsWebUiHost.Run(provider, new RunicCsWebUiHostOptions { MaximumUploadBytes = 4 * 1_048_576 }, async host =>
        {
            await using var window = await host.OpenWindowAsync<BlobProbeWindow>(new RunicWindowOptions { ContentRoot = root.FullName });
            model = (BlobProbeViewModel)window.DataContext!;
            await window.WaitForCloseAsync();
            return 0;
        });
        if (result != 0) return result;
        if (model?.Received is not { Length: 1_000_000 } received || received.Any(static value => value != (byte)'x') || model.ReceivedName != "probe.txt")
            throw new InvalidOperationException($"The attach command received {model?.Received?.Length} bytes named {model?.ReceivedName}.");
        Console.WriteLine("CSWEBUI_BLOBS_OK");
        return 0;
    }
    finally
    {
        root.Delete(recursive: true);
    }
}

// #726 and #730: a Window serves an asset source through its file handler, without a folder. One folder of an
// archive is served as a frontend of its own; a path the source does not declare answers 404 instead of a file of the
// working directory, WebUI's default root; and WebUI's authentication cookie, which a file handler turns off, still
// binds the window to the first client that loaded webui.js.
static async Task ProbeEmbeddedAssetsAsync()
{
    var work = Directory.CreateTempSubdirectory("runic-cswebui-assets-");
    var previousDirectory = Environment.CurrentDirectory;
    try
    {
        File.WriteAllText(Path.Combine(work.FullName, "outside.txt"), "outside");
        Environment.CurrentDirectory = work.FullName;
        using var archive = new MemoryStream();
        await Runic.Assets.AssetArchive.WriteAsync(new MemoryAssetSource(new Dictionary<string, string>
        {
            ["index.html"] = "<!doctype html><html><body>main</body></html>",
            ["settings/index.html"] = "<!doctype html><html><head><script src=\"/webui.js\"></script></head><body>settings</body></html>",
            ["settings/app.css"] = "body { color: black; }",
        }), archive);
        archive.Position = 0;
        var settings = Runic.Assets.AssetSourceExtensions.Subdirectory(Runic.Assets.AssetArchive.Read(archive), "settings");

        var services = new ServiceCollection();
        services.AddScoped<ProbeViewModel>();
        services.AddRunicViews();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new RunicCsWebUiHost(provider, new RunicCsWebUiHostOptions { Assets = settings });
        await using (var window = await host.OpenWindowAsync<ProbeWindow>())
        {
            var url = CsWebUiPlatformServices.GetWebUiWindow(window)?.Url
                ?? throw new InvalidOperationException("The serve-only window has no URL.");
            // WebUI listens on the loopback address, whatever this machine resolves localhost to.
            var root = new UriBuilder(url) { Host = "127.0.0.1", Path = "/" }.Uri;
            using var first = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new System.Net.CookieContainer() }) { BaseAddress = root };
            using var page = await first.GetAsync("index.html");
            var html = await page.Content.ReadAsStringAsync();
            if (page.StatusCode != System.Net.HttpStatusCode.OK || !html.Contains("settings", StringComparison.Ordinal) ||
                page.Content.Headers.ContentType?.ToString() != "text/html; charset=utf-8" || page.Headers.ETag is null)
                throw new InvalidOperationException($"The start page answered {(int)page.StatusCode} {page.Content.Headers.ContentType}: {html}");
            using var style = await first.GetAsync("app.css");
            if (style.StatusCode != System.Net.HttpStatusCode.OK || await style.Content.ReadAsStringAsync() != "body { color: black; }")
                throw new InvalidOperationException($"app.css answered {(int)style.StatusCode}.");
            using var outside = await first.GetAsync("outside.txt");
            if (outside.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new InvalidOperationException($"A path outside the source answered {(int)outside.StatusCode} instead of 404.");
            using var script = await first.GetAsync("webui.js");
            if (script.StatusCode != System.Net.HttpStatusCode.OK || !script.Headers.Contains("Set-Cookie"))
                throw new InvalidOperationException($"webui.js answered {(int)script.StatusCode} without WebUI's authentication cookie.");
            using var second = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = root };
            using var other = await second.GetAsync("index.html");
            if (other.StatusCode != System.Net.HttpStatusCode.Forbidden)
                throw new InvalidOperationException($"A second client without the cookie answered {(int)other.StatusCode} instead of 403.");
            using var again = await first.GetAsync("index.html");
            if (again.StatusCode != System.Net.HttpStatusCode.OK)
                throw new InvalidOperationException($"The first client answered {(int)again.StatusCode} after loading webui.js.");
        }
        Console.WriteLine("CSWEBUI_EMBEDDED_ASSETS_OK");
    }
    finally
    {
        Environment.CurrentDirectory = previousDirectory;
        work.Delete(recursive: true);
    }
}

// The WebUI library CsWebUi.Native bundles: beside the executable when published, under runtimes/<rid>/native otherwise.
static string BundledNativeLibrary()
{
    var name = OperatingSystem.IsWindows() ? "webui-2.dll" : OperatingSystem.IsMacOS() ? "libwebui-2.dylib" : "libwebui-2.so";
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, name),
        Path.Combine(AppContext.BaseDirectory, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native", name),
    };
    return candidates.FirstOrDefault(File.Exists) ??
        throw new InvalidOperationException("The bundled WebUI library was not found at " + string.Join(" or ", candidates));
}

static void RequireShowFailure(string browserPath, Func<RunicWindowDiagnostic, WebUiException, bool> expected)
{
    Environment.SetEnvironmentVariable(WebUiApplication.BrowserPathEnvironmentVariable, browserPath);
    var services = new ServiceCollection();
    services.AddScoped<ProbeViewModel>();
    services.AddRunicViews();
    var log = new RecordingLoggerFactory();
    services.AddSingleton<ILoggerFactory>(log);
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    RunicWindowConfigurationException? failure = null;
    var result = RunicCsWebUiHost.Run(provider, async host =>
    {
        WebUiApplication.SetConnectionTimeout(2);
        // Validation reports a selected browser that does not exist, which every open fails on, and accepts one that
        // exists: whether it starts and connects shows only when a window opens.
        var validation = host.ValidateWindow<ProbeWindow>();
        var exists = File.Exists(browserPath);
        if (exists ? !validation.IsValid : validation.Errors is not [{ Code: "browser-not-found" } missing] ||
            !missing.Message.Contains(WebUiApplication.BrowserPathEnvironmentVariable, StringComparison.Ordinal))
            throw new InvalidOperationException($"ValidateWindow with the browser {browserPath} reported: " +
                string.Join(", ", validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", "1");
        if (!host.ValidateWindow<ProbeWindow>().IsValid)
            throw new InvalidOperationException($"ValidateWindow in serve-only mode checked the browser {browserPath}.");
        Environment.SetEnvironmentVariable("RUNIC_APPLICATION_SERVE_ONLY", null);
        try
        {
            await using var window = await host.OpenWindowAsync<ProbeWindow>();
        }
        catch (RunicWindowConfigurationException error)
        {
            failure = error;
            throw;
        }
        Console.Error.WriteLine($"The Window opened with the browser {browserPath}.");
        return 3;
    });
    if (result != 1)
        throw new InvalidOperationException($"Run returned {result} instead of 1 for a window that could not be shown with {browserPath}.");
    if (failure is not { Diagnostics: [var diagnostic], InnerException: WebUiException error } ||
        diagnostic.Message != error.Message || !error.Message.Contains(browserPath, StringComparison.Ordinal) ||
        !expected(diagnostic, error))
        throw new InvalidOperationException($"OpenWindowAsync did not report the browser {browserPath} that could not show the window:\n{failure}");
    LogEntry[] entries;
    lock (log.Entries) entries = [.. log.Entries];
    if (entries.Count(static entry => entry.EventId.Id == 2100) != 1 || !entries.Any(entry =>
            entry is { Category: "Runic.Application.Views.CsWebUi", Level: LogLevel.Error } &&
            entry.EventId is { Id: 2100, Name: "CsWebUiWindowShowFailed" } &&
            entry.Message.Contains(nameof(ProbeWindow), StringComparison.Ordinal) &&
            entry.Message.Contains(diagnostic.Code, StringComparison.Ordinal) &&
            entry.Message.Contains(browserPath, StringComparison.Ordinal)))
        throw new InvalidOperationException($"The window that could not be shown with {browserPath} was not logged once as event 2100:\n" +
            string.Join('\n', entries.Select(static entry => entry.ToString())));
}

static void RequireBridgeNotRegistered(RunicWindowConfigurationException error, string name)
{
    if (error.Diagnostics is [{ Code: RunicWindowConfigurationException.BridgeNotRegisteredCode }] &&
        error.Message.Contains("- bridge-not-registered: ", StringComparison.Ordinal) &&
        error.Message.Contains("AddRunicViews()", StringComparison.Ordinal) &&
        error.Message.Contains(typeof(ProbeViewModel).FullName!, StringComparison.Ordinal))
        return;
    throw new InvalidOperationException($"{name} did not report the missing Bridge with its code and remediation:\n{error}");
}

namespace CsWebUiWindowProbes
{
    public sealed partial class ProbeViewModel : ObservableObject
    {
        [ObservableProperty] private int count;
    }

    public sealed partial class ProbeWindow : RunicWindow<ProbeViewModel>;

    // blobs/1: a 1x1 PNG avatar and a command that reads an upload.
    public sealed partial class BlobProbeViewModel : ObservableObject
    {
        private static readonly byte[] Pixel = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

        [ObservableProperty]
        public partial Runic.Application.Views.RunicBlob Avatar { get; private set; } =
            Runic.Application.Views.RunicBlob.FromBytes(Pixel, "image/png", "avatar.png");

        [Runic.Application.Views.RunicIgnore] public byte[]? Received { get; private set; }
        [Runic.Application.Views.RunicIgnore] public string? ReceivedName { get; private set; }

        [RelayCommand]
        private async Task AttachAsync(Runic.Application.Views.RunicUpload file, CancellationToken cancellationToken)
        {
            await using var stream = await file.OpenReadAsync(cancellationToken);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken);
            Received = copy.ToArray();
            ReceivedName = file.FileName;
        }
    }

    public sealed partial class BlobProbeWindow : RunicWindow<BlobProbeViewModel>;

    internal static class DesktopMenuProbe
    {
        internal static Runic.Platform.DesktopMenu Menu(string title, params Runic.Platform.DesktopMenuEntry[] items) => new(title, [.. items]);
    }

    // What the menu commands ran, and the host whose bar the mode command replaces.
    public sealed class MenuProbe
    {
        internal RunicCsWebUiHost? Host { get; set; }
        internal int Saves;
        internal int Locks;
        internal int Undos;
        internal List<string> Modes { get; } = [];

        // Edit is declared before File, which the page receives first on Windows and Linux. Undo is disabled in the
        // declaration although its command can run.
        internal static IReadOnlyList<Runic.Platform.DesktopMenu> Bar(string mode) =>
        [
            DesktopMenuProbe.Menu("Edit", new Runic.Platform.DesktopMenuItem("Undo", "edit.undo") { IsEnabled = false }),
            new("File", [
                new Runic.Platform.DesktopMenuItem("Save", "file.save")
                    { AccessKey = 'S', Shortcut = new Runic.Platform.DesktopShortcut("S") },
                new Runic.Platform.DesktopMenuSeparator(),
                new Runic.Platform.DesktopMenuItem("Lock", "file.lock"),
            ]) { Role = Runic.Platform.DesktopMenuRole.File },
            DesktopMenuProbe.Menu("View",
                new Runic.Platform.DesktopMenuItem("Mode A", "view.mode") { RadioGroup = "mode", IsChecked = mode == "a", CommandParameter = "a" },
                new Runic.Platform.DesktopMenuItem("Mode B", "view.mode") { RadioGroup = "mode", IsChecked = mode == "b", CommandParameter = "b" }),
        ];
    }

    // Save runs until Lock locks the document; Mode replaces the bar with the chosen mode checked.
    public sealed partial class MenuProbeViewModel(MenuProbe probe) : ObservableObject, IRunicMenuCommandSource
    {
        [ObservableProperty] private bool locked;

        public ICommand? GetMenuCommand(string commandId) => commandId switch
        {
            "file.save" => SaveCommand,
            "file.lock" => LockCommand,
            "edit.undo" => UndoCommand,
            "view.mode" => ModeCommand,
            _ => null,
        };

        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save() => Interlocked.Increment(ref probe.Saves);

        private bool CanSave() => !Locked;

        [RelayCommand]
        private void Lock()
        {
            Interlocked.Increment(ref probe.Locks);
            Locked = true;
            SaveCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void Undo() => Interlocked.Increment(ref probe.Undos);

        [RelayCommand]
        private void Mode(string? mode)
        {
            lock (probe.Modes) probe.Modes.Add(mode ?? "");
            probe.Host!.SetMenuBar(MenuProbe.Bar(mode ?? ""));
        }
    }

    public sealed partial class MenuProbeWindow : RunicWindow<MenuProbeViewModel>;

    public sealed partial class GuardedProbeViewModel : ObservableObject
    {
        [ObservableProperty] private bool dirty;
    }

    // Guards its close and answers CanCloseAsync with Answer, counting the calls.
    public sealed partial class GuardedProbeWindow : RunicWindow<GuardedProbeViewModel>
    {
        public GuardedProbeWindow() => GuardClose = true;
        public bool Answer { get; set; }
        public int Asked { get; private set; }

        protected override async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
        {
            Asked++;
            return await base.CanCloseAsync(cancellationToken) && Answer;
        }
    }

    // Hides IServiceProviderIsService, as some third-party containers do.
    internal sealed class OpaqueProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? null : inner.GetService(serviceType);
    }

    internal sealed record LogEntry(string Category, EventId EventId, LogLevel Level, string Message);

    internal sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class RecordingLogger(RecordingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add(new LogEntry(category, eventId, logLevel, formatter(state, exception)));
            }
        }
    }

    // An in-memory asset source for AssetArchive.WriteAsync, which needs no directory handles on any platform.
    internal sealed class MemoryAssetSource : Runic.Assets.IAssetSource
    {
        private readonly Dictionary<string, byte[]> _content;

        public MemoryAssetSource(IReadOnlyDictionary<string, string> files)
        {
            _content = files.ToDictionary(static file => file.Key, static file => System.Text.Encoding.UTF8.GetBytes(file.Value), StringComparer.Ordinal);
            Manifest = new Runic.Assets.AssetManifest(_content.Select(static file => new Runic.Assets.AssetDescriptor(
                file.Key, file.Key.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html", file.Value.Length,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file.Value)), isEntryPoint: file.Key == "index.html")));
        }

        public Runic.Assets.AssetManifest Manifest { get; }

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Runic.Assets.AssetReadSnapshot> OpenAsync(string relativePath, CancellationToken cancellationToken = default) =>
            Manifest.TryGetAsset(relativePath, out var descriptor)
                ? ValueTask.FromResult(new Runic.Assets.AssetReadSnapshot(descriptor, new MemoryStream(_content[descriptor.RelativePath], writable: false)))
                : throw new FileNotFoundException("The asset is not declared.", relativePath);
    }

    internal sealed class ScopeProbe(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
