using CommunityToolkit.Mvvm.ComponentModel;
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
    default:
        Console.Error.WriteLine("Usage: CsWebUiWindowProbes --probe-factory-failure | --probe-window-close | --probe-close-guard | --probe-missing-bridge | --probe-show-failure | --probe-platform-services");
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
            is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noOwner))
        throw new InvalidOperationException("A CS-WebUI window service did not report the missing native owner.");
    if (await provider.GetRequiredService<Runic.Platform.IDesktopSettings>().ReadAsync() is not Runic.Platform.PlatformResult<Runic.Platform.DesktopAppearance>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopNotifications>().RequestPermissionAsync() is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopStatusIcon>().HideAsync() is not Runic.Platform.PlatformResult<Runic.Platform.PlatformUnit>.Unavailable(noProvider)
        || await provider.GetRequiredService<Runic.Platform.IDesktopLoginStartup>().IsEnabledAsync() is not Runic.Platform.PlatformResult<bool>.Unavailable(noProvider))
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
static int ProbeCloseGuard()
{
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

    internal sealed class ScopeProbe(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
