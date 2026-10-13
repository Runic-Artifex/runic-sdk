using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;

// IRunicWindowService on Runic Desktop: a ViewModel opens another Window on the host that runs the application, the
// new Window binds its own platform services, and closing it natively releases its scope.
internal sealed class WindowServiceChecks
{
    [Test, NotInParallel(Order = 19)]
    public async Task ViewModelOpensAWindowThatIsReleasedWhenTheUserClosesIt()
    {
        var services = new ServiceCollection();
        services.AddScoped<OpenerViewModel>();
        services.AddScoped<Func<IBridgeTransport, OpenerViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddScoped<OpenedViewModel>();
        services.AddScoped<Func<IBridgeTransport, OpenedViewModel, IDisposable>>(_ => static (_, _) => new NoopAttachment());
        services.AddScoped<ReleaseProbe>();
        services.AddRunicPlatformServices();
        services.AddRunicWindowService();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var factory = new NativeDispatchFactory();
        await using var desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });

        await using var opener = await TestWindows.EmbeddedHtml(provider, desktop).OpenWindowAsync<OpenerWindow>();
        var opened = await opener.ViewModel.OpenAsync();
        var platform = (DesktopWindowPlatformServices)opened.ViewModel.Platform;
        Require(factory.Hosts.Count == 2 && opened.Host is DesktopBridgeWindow<OpenedViewModel> && platform.IsBound
            && !ReferenceEquals(opened.ViewModel.Scope, opener.ViewModel.Scope),
            "The window service did not open a native Desktop window with its own scope and bound platform services.");

        // The user closes the native window. Nothing awaits it, so the service releases its scope.
        await factory.Hosts[1].CloseAsync();
        await opened.ViewModel.Scope.Released.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!opener.ViewModel.Scope.Released.IsCompleted, "Closing the opened window released the opener's scope.");
        Require(platform.GetSnapshot().Statuses.Values.All(static status => status is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)),
            "The released window's platform services did not stop.");
    }

    [Test, NotInParallel(Order = 19)]
    public async Task PlatformServicesRegisterOneDesktopSettingsForTheApplication()
    {
        var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var settings = provider.GetRequiredService<IDesktopSettings>();
        await using (var first = provider.CreateAsyncScope())
        await using (var second = provider.CreateAsyncScope())
            Require(ReferenceEquals(settings, first.ServiceProvider.GetRequiredService<IDesktopSettings>())
                && ReferenceEquals(settings, second.ServiceProvider.GetRequiredService<IDesktopSettings>()),
                "Two Window scopes got different desktop settings.");
        if (OperatingSystem.IsLinux())
            Require(settings.GetType().FullName == "Runic.Platform.Linux.Portal.PortalDesktopSettings",
                $"Linux did not read the portal settings: {settings.GetType().FullName}");
        // Disposing the provider stops the settings; nothing is read here, so no desktop service is needed.
        await provider.DisposeAsync();
        Require(await CatchAsync(() => settings.ReadAsync().AsTask()) is ObjectDisposedException,
            "Disposing the service provider did not dispose the desktop settings.");

        await using var custom = DesktopSettings.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopSettings>(), custom), "AddRunicPlatformServices replaced the application's settings.");
    }

    // Notifications come from AddRunicPlatformServices with no OperatingSystem branch in the application. Nothing here
    // sends a notification or connects to a session bus: creating the service connects to nothing, and no request
    // is made of a native provider.
    [Test, NotInParallel(Order = 19)]
    public async Task PlatformServicesRegisterOneDesktopNotificationsForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var notifications = provider.GetRequiredService<IDesktopNotifications>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(notifications, first.ServiceProvider.GetRequiredService<IDesktopNotifications>()),
                    "A Window scope got its own desktop notifications.");
            if (OperatingSystem.IsLinux())
                Require(PortalApplicationId(notifications, out var id) && id is null,
                    $"Linux without an application ID did not create portal notifications with inferred identity: {notifications.GetType().FullName}");
            else if (OperatingSystem.IsWindows())
                Require(await notifications.RequestPermissionAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "Windows without an AppUserModelID did not report notifications unconfigured.");
        }

        await using (var identified = new ServiceCollection().AddRunicPlatformServices("org.example.Notes").BuildServiceProvider())
        {
            var notifications = identified.GetRequiredService<IDesktopNotifications>();
            if (OperatingSystem.IsLinux())
                Require(PortalApplicationId(notifications, out var id) && id == "org.example.Notes",
                    "Linux portal notifications did not use the application ID passed to AddRunicPlatformServices.");
            else if (OperatingSystem.IsWindows())
                Require(notifications.GetType().FullName == "Runic.Platform.Windows.WindowsDesktopNotifications",
                    $"Windows with an AppUserModelID did not create toasts: {notifications.GetType().FullName}");
        }

        Require(Catch(() => new ServiceCollection().AddRunicPlatformServices(" ")) is ArgumentException,
            "An empty application ID was accepted.");
        if (OperatingSystem.IsLinux())
            Require(Catch(() => new ServiceCollection().AddRunicPlatformServices("Notes")) is ArgumentException,
                "Linux accepted an application ID that is not a reverse-DNS desktop ID.");

        await using var custom = DesktopNotifications.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopNotifications>(), custom),
            "AddRunicPlatformServices replaced the application's notifications.");
    }

    // The status icon is an application singleton like notifications. Creating it connects to nothing; only
    // ShowAsync registers with the desktop's status area.
    [Test, NotInParallel(Order = 30)]
    public async Task PlatformServicesRegisterOneStatusIconForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var icon = provider.GetRequiredService<IDesktopStatusIcon>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(icon, first.ServiceProvider.GetRequiredService<IDesktopStatusIcon>()),
                    "A Window scope got its own status icon.");
            if (OperatingSystem.IsLinux())
                Require(icon.GetType().FullName == "Runic.Platform.Linux.Portal.StatusNotifierIcon",
                    $"Linux did not create a StatusNotifierItem: {icon.GetType().FullName}");
            else
                Require(await icon.HideAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "A platform without a status icon provider did not report it unconfigured.");
        }

        await using var custom = DesktopStatusIcons.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopStatusIcon>(), custom),
            "AddRunicPlatformServices replaced the application's status icon.");
    }

    [Test]
    public async Task UnavailableDesktopNotificationsReportTheReason()
    {
        var notifications = DesktopNotifications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        var raised = false;
        notifications.Activated += (_, _) => raised = true;
        Require(await notifications.RequestPermissionAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)
            && await notifications.ShowAsync(new DesktopNotification("saved", "Notes", "Saved.")) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)
            && await notifications.RemoveAsync("saved") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)
            && !raised,
            "Unavailable notifications did not report the provider unconfigured for every request.");
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Require(await CatchAsync(() => notifications.ShowAsync(new("saved", "Notes", "Saved."), cancelled.Token).AsTask()) is OperationCanceledException,
                "A cancelled request did not throw.");
        }
        await notifications.DisposeAsync();
        Require(await CatchAsync(() => notifications.RemoveAsync("saved").AsTask()) is ObjectDisposedException, "A request after disposal did not fail.");
        Require(Catch(() => DesktopNotifications.Unavailable((PlatformUnavailableReason)99)) is ArgumentOutOfRangeException,
            "An undefined reason was accepted.");
    }

    // Reads the identity of portal notifications without making a request.
    private static bool PortalApplicationId(IDesktopNotifications notifications, out string? id)
    {
        id = null;
        if (notifications.GetType().FullName != "Runic.Platform.Linux.Portal.PortalNotifications") return false;
        var application = notifications.GetType()
            .GetField("_application", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
            .GetValue(notifications);
        if (application is null) return false;
        id = application.GetType().GetProperty("ApplicationId")?.GetValue(application) as string;
        return true;
    }

    [Test]
    public async Task UnavailableDesktopSettingsReportTheReason()
    {
        var settings = DesktopSettings.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Require(await settings.ReadAsync() is PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
            "A read did not report the provider unconfigured.");
        using var stop = new CancellationTokenSource();
        var watched = new List<PlatformResult<DesktopAppearance>>();
        var watch = Task.Run(async () =>
        {
            await foreach (var result in settings.WatchAsync(stop.Token)) watched.Add(result);
        });
        await WaitUntilAsync(() => watched.Count == 1);
        Require(!watch.IsCompleted, "The watch ended without cancellation.");
        await settings.DisposeAsync();
        Require(await CatchAsync(() => watch.WaitAsync(TimeSpan.FromSeconds(5))) is OperationCanceledException
            && watched is [PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured)],
            "Disposal did not end the watch after its one unavailable value.");
        Require(await CatchAsync(() => settings.ReadAsync().AsTask()) is ObjectDisposedException, "A read after disposal did not fail.");
        Require(Catch(() => DesktopSettings.Unavailable((PlatformUnavailableReason)99)) is ArgumentOutOfRangeException,
            "An undefined reason was accepted.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold within five seconds.");
            await Task.Delay(10);
        }
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> CatchAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// Completes when its Window scope is disposed.
sealed class ReleaseProbe : IAsyncDisposable
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Released => _released.Task;
    public ValueTask DisposeAsync()
    {
        _released.TrySetResult();
        return ValueTask.CompletedTask;
    }
}

// Opens a file window from a command, as an application ViewModel does.
sealed class OpenerViewModel(IRunicWindowService windows, ReleaseProbe scope) : INotifyPropertyChanged
{
    public ReleaseProbe Scope { get; } = scope;
    public Task<OpenedWindow> OpenAsync() => windows.OpenWindowAsync<OpenedWindow>().AsTask();
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class OpenedViewModel(IWindowPlatformServices platform, ReleaseProbe scope) : INotifyPropertyChanged
{
    public IWindowPlatformServices Platform { get; } = platform;
    public ReleaseProbe Scope { get; } = scope;
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class OpenerWindow : RunicWindow<OpenerViewModel>;
sealed class OpenedWindow : RunicWindow<OpenedViewModel>;
