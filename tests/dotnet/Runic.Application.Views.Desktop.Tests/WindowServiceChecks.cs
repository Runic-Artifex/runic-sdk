using System.Collections.Immutable;
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

    // Login startup is an application singleton too. Nothing is registered with the desktop by resolving it.
    [Test, NotInParallel(Order = 31)]
    public async Task PlatformServicesRegisterOneLoginStartupForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var startup = provider.GetRequiredService<IDesktopLoginStartup>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(startup, first.ServiceProvider.GetRequiredService<IDesktopLoginStartup>()),
                    "A Window scope got its own login startup.");
            if (OperatingSystem.IsLinux())
                Require(startup.GetType().FullName == "Runic.Platform.Linux.Portal.LinuxLoginStartup",
                    $"Linux did not create XDG login startup: {startup.GetType().FullName}");
            else
                Require(await startup.IsEnabledAsync() is PlatformResult<bool>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "Login startup without an application ID was not reported unconfigured.");
        }

        var custom = DesktopLoginStartup.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopLoginStartup>(), custom),
            "AddRunicPlatformServices replaced the application's login startup.");
    }

    // Volumes are an application singleton that needs no window. Resolving them connects to nothing.
    [Test, NotInParallel(Order = 33)]
    public async Task PlatformServicesRegisterOneVolumesForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var volumes = provider.GetRequiredService<IDesktopVolumes>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(volumes, first.ServiceProvider.GetRequiredService<IDesktopVolumes>()), "A Window scope got its own volumes.");
            if (OperatingSystem.IsLinux())
                Require(volumes.GetType().FullName == "Runic.Platform.Linux.Portal.UDisksVolumes", $"Linux did not create UDisks2 volumes: {volumes.GetType().FullName}");
            else if (OperatingSystem.IsWindows())
                Require(volumes.GetType().FullName == "Runic.Platform.Windows.WindowsVolumes", $"Windows did not create its volumes: {volumes.GetType().FullName}");
            else if (OperatingSystem.IsMacOS())
                Require(volumes.GetType().FullName == "Runic.Platform.MacOS.MacVolumes", $"macOS did not create Disk Arbitration volumes: {volumes.GetType().FullName}");
        }

        await using var custom = DesktopVolumes.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopVolumes>(), custom), "AddRunicPlatformServices replaced the application's volumes.");
    }

    // Secret storage is an application singleton that needs no window. Resolving it connects to nothing.
    [Test, NotInParallel(Order = 33)]
    public async Task PlatformServicesRegisterOneSecretStoreForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var store = provider.GetRequiredService<IDesktopSecretStore>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(store, first.ServiceProvider.GetRequiredService<IDesktopSecretStore>()),
                    "A Window scope got its own secret store.");
            string expected = OperatingSystem.IsLinux() ? "Runic.Platform.Linux.Portal.SecretServiceStore"
                : OperatingSystem.IsWindows() ? "Runic.Platform.Windows.WindowsSecretStore"
                : OperatingSystem.IsMacOS() ? "Runic.Platform.MacOS.MacSecretStore" : "";
            Require(store.GetType().FullName == expected || expected.Length == 0,
                $"The operating system's secret store was not created: {store.GetType().FullName}");
        }

        var custom = DesktopSecretStore.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopSecretStore>(), custom),
            "AddRunicPlatformServices replaced the application's secret store.");
        Require(await custom.GetAsync("org.example.App", "user") is PlatformResult<string?>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && await custom.SetAsync("org.example.App", "user", "s") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && await custom.DeleteAsync("org.example.App", "user") is PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "The unavailable secret store did not report its reason.");
        // Arguments are checked as a provider checks them.
        Require(await CatchAsync(() => custom.GetAsync("", "user").AsTask()) is ArgumentException
            && await CatchAsync(() => custom.SetAsync("s", "a\0", "x").AsTask()) is ArgumentException,
            "The unavailable secret store accepted invalid names.");
    }

    // The trash is an application singleton that needs no window. Resolving it touches nothing.
    [Test, NotInParallel(Order = 34)]
    public async Task PlatformServicesRegisterOneTrashForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var trash = provider.GetRequiredService<IDesktopTrash>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(trash, first.ServiceProvider.GetRequiredService<IDesktopTrash>()),
                    "A Window scope got its own trash.");
            string expected = OperatingSystem.IsLinux() ? "Runic.Platform.Linux.Portal.LinuxTrash"
                : OperatingSystem.IsWindows() ? "Runic.Platform.Windows.WindowsTrash"
                : OperatingSystem.IsMacOS() ? "Runic.Platform.MacOS.MacTrash" : "";
            Require(trash.GetType().FullName == expected || expected.Length == 0,
                $"The operating system's trash was not created: {trash.GetType().FullName}");
        }

        var custom = DesktopTrash.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopTrash>(), custom), "AddRunicPlatformServices replaced the application's trash.");
        string path = Path.Combine(Path.GetTempPath(), "runic-not-trashed");
        Require(await custom.MoveToTrashAsync(path) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "The unavailable trash did not report its reason.");
        Require(await CatchAsync(() => custom.MoveToTrashAsync("relative").AsTask()) is ArgumentException,
            "The unavailable trash accepted a relative path.");
    }

    // File transfers are an application singleton: the managed engine on every operating system.
    [Test, NotInParallel(Order = 35)]
    public async Task PlatformServicesRegisterOneFileTransferEngineForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var transfers = provider.GetRequiredService<IDesktopFileTransfers>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(transfers, first.ServiceProvider.GetRequiredService<IDesktopFileTransfers>()),
                    "A Window scope got its own file transfers.");
            Require(transfers.GetType().FullName == "Runic.Platform.Runtime.FileTransferEngine",
                $"The managed engine was not created: {transfers.GetType().FullName}");
        }
        var custom = DesktopFileTransfers.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopFileTransfers>(), custom), "AddRunicPlatformServices replaced the application's file transfers.");
    }

    // The application database is an application singleton that needs no window. Resolving it reads nothing.
    [Test, NotInParallel(Order = 36)]
    public async Task PlatformServicesRegisterOneApplicationDatabaseForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var applications = provider.GetRequiredService<IDesktopApplications>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(applications, first.ServiceProvider.GetRequiredService<IDesktopApplications>()),
                    "A Window scope got its own application database.");
            string expected = OperatingSystem.IsLinux() ? "Runic.Platform.Linux.Portal.LinuxDesktopApplications"
                : OperatingSystem.IsWindows() ? "Runic.Platform.Windows.WindowsDesktopApplications"
                : OperatingSystem.IsMacOS() ? "Runic.Platform.MacOS.MacDesktopApplications" : "";
            if (expected.Length != 0)
                Require(applications.GetType().FullName == expected, $"The operating system's application database was not created: {applications.GetType().FullName}");
            else
                Require(await applications.GetApplicationsAsync() is PlatformResult<System.Collections.Immutable.ImmutableArray<DesktopApplicationInfo>>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "A platform without an application database provider did not report it unconfigured.");
        }

        var custom = DesktopApplications.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopApplications>(), custom),
            "AddRunicPlatformServices replaced the application's application database.");
    }

    // Thumbnails are an application singleton that needs no window. Resolving them reads nothing and connects to nothing.
    [Test, NotInParallel(Order = 37)]
    public async Task PlatformServicesRegisterOneThumbnailProviderForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var thumbnails = provider.GetRequiredService<IDesktopThumbnails>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(thumbnails, first.ServiceProvider.GetRequiredService<IDesktopThumbnails>()),
                    "A Window scope got its own thumbnails.");
            string expected = OperatingSystem.IsLinux() ? "Runic.Platform.Linux.Portal.LinuxThumbnails"
                : OperatingSystem.IsWindows() ? "Runic.Platform.Windows.WindowsThumbnails"
                : OperatingSystem.IsMacOS() ? "Runic.Platform.MacOS.MacThumbnails" : "";
            Require(thumbnails.GetType().FullName == expected || expected.Length == 0,
                $"The operating system's thumbnails were not created: {thumbnails.GetType().FullName}");
        }

        var custom = DesktopThumbnails.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopThumbnails>(), custom), "AddRunicPlatformServices replaced the application's thumbnails.");
        string path = Path.Combine(Path.GetTempPath(), "runic-no-thumbnail.png");
        Require(await custom.GetThumbnailAsync(path, 128) is PlatformResult<DesktopThumbnail?>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "The unavailable thumbnails did not report their reason.");
        Require(await CatchAsync(() => custom.GetThumbnailAsync("relative.png", 128).AsTask()) is ArgumentException
            && await CatchAsync(() => custom.GetThumbnailAsync(path, DesktopThumbnails.MaximumSize + 1).AsTask()) is ArgumentOutOfRangeException,
            "The unavailable thumbnails accepted a request a provider refuses.");
    }

    [Test]
    public async Task UnavailableApplicationDatabaseReportsTheReason()
    {
        var applications = DesktopApplications.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        const PlatformUnavailableReason reason = PlatformUnavailableReason.ProviderNotConfigured;
        Require(await applications.GetContentTypeAsync("/tmp/notes.txt") is PlatformResult<DesktopContentType>.Unavailable(reason)
            && await applications.GetApplicationsAsync() is PlatformResult<System.Collections.Immutable.ImmutableArray<DesktopApplicationInfo>>.Unavailable(reason)
            && await applications.GetApplicationsForAsync("text/plain") is PlatformResult<System.Collections.Immutable.ImmutableArray<DesktopApplicationInfo>>.Unavailable(reason)
            && await applications.GetDefaultApplicationAsync("text/plain") is PlatformResult<DesktopApplicationInfo?>.Unavailable(reason)
            && await applications.SetDefaultApplicationAsync("text/plain", "org.example.Editor.desktop") is PlatformResult<PlatformUnit>.Unavailable(reason)
            && await applications.LaunchAsync("org.example.Editor.desktop", ["/tmp/notes.txt", "https://example.org/"]) is PlatformResult<PlatformUnit>.Unavailable(reason)
            && await applications.GetIconAsync(DesktopIconRequest.ForIconName("folder", 48)) is PlatformResult<DesktopIconData?>.Unavailable(reason),
            "The unavailable application database did not report its reason for every request.");
        // Arguments are checked as a provider checks them.
        Require(await CatchAsync(() => applications.GetContentTypeAsync("").AsTask()) is ArgumentException
            && await CatchAsync(() => applications.GetContentTypeAsync("a\0b").AsTask()) is ArgumentException
            && await CatchAsync(() => applications.GetContentTypeAsync("a", (DesktopContentDetection)7).AsTask()) is ArgumentOutOfRangeException
            && await CatchAsync(() => applications.GetApplicationsForAsync(" ").AsTask()) is ArgumentException
            && await CatchAsync(() => applications.SetDefaultApplicationAsync("text/plain", "").AsTask()) is ArgumentException
            && await CatchAsync(() => applications.LaunchAsync("org.example.Editor.desktop", default).AsTask()) is ArgumentException
            && await CatchAsync(() => applications.LaunchAsync("org.example.Editor.desktop", ["relative/notes.txt"]).AsTask()) is ArgumentException
            && await CatchAsync(() => applications.GetIconAsync(null!).AsTask()) is ArgumentNullException,
            "The unavailable application database accepted a request a provider refuses.");
        Require(Catch(() => DesktopIconRequest.ForIconName("icons/folder", 48)) is ArgumentException
            && Catch(() => DesktopIconRequest.ForContentType("text/plain", 0)) is ArgumentOutOfRangeException
            && Catch(() => DesktopIconRequest.ForApplication("org.example.Editor.desktop", 48, scale: 17)) is ArgumentOutOfRangeException
            && DesktopIconRequest.ForApplication("org.example.Editor.desktop", 48) is { ApplicationId: "org.example.Editor.desktop", IconName: null, ContentType: null, Scale: 1 },
            "Icon requests did not check their values.");
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Require(await CatchAsync(() => applications.GetApplicationsAsync(cancelled.Token).AsTask()) is OperationCanceledException,
                "A cancelled request did not throw.");
        }
        Require(Catch(() => DesktopApplications.Unavailable((PlatformUnavailableReason)99)) is ArgumentOutOfRangeException,
            "An undefined reason was accepted.");
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
            else if (OperatingSystem.IsWindows())
                Require(icon.GetType().FullName == "Runic.Platform.Windows.WindowsStatusIcon"
                    && await icon.HideAsync() is PlatformResult<PlatformUnit>.Success,
                    $"Windows did not create a notification-area icon: {icon.GetType().FullName}");
            else
                Require(await icon.HideAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "A platform without a status icon provider did not report it unconfigured.");
        }

        await using var custom = DesktopStatusIcon.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopStatusIcon>(), custom),
            "AddRunicPlatformServices replaced the application's status icon.");
    }

    // Global shortcuts are an application singleton like the status icon. Creating them registers no key; only BindAsync
    // does, so nothing here touches the session's keyboard.
    [Test, NotInParallel(Order = 31)]
    public async Task PlatformServicesRegisterOneGlobalShortcutsForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var shortcuts = provider.GetRequiredService<IDesktopGlobalShortcuts>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(shortcuts, first.ServiceProvider.GetRequiredService<IDesktopGlobalShortcuts>()),
                    "A Window scope got its own global shortcuts.");
            // Linux connects to no bus until BindAsync, so only the type is checked there.
            if (OperatingSystem.IsLinux())
                Require(shortcuts.GetType().FullName == "Runic.Platform.Linux.Portal.PortalGlobalShortcuts",
                    $"Linux did not create the portal's global shortcuts: {shortcuts.GetType().FullName}");
            var expected = OperatingSystem.IsWindows() ? "Runic.Platform.Windows.WindowsGlobalShortcuts"
                : OperatingSystem.IsMacOS() ? "Runic.Platform.MacOS.MacGlobalShortcuts" : null;
            if (expected is not null)
                Require(shortcuts.GetType().FullName == expected
                    && await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success({ IsEmpty: true }),
                    $"The operating system's global shortcuts were not registered: {shortcuts.GetType().FullName}");
            else if (!OperatingSystem.IsLinux())
                Require(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "A platform without a global shortcut provider did not report it unconfigured.");
        }

        await using var custom = DesktopGlobalShortcuts.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopGlobalShortcuts>(), custom),
            "AddRunicPlatformServices replaced the application's global shortcuts.");
    }

    // Inhibition is an application singleton: a lease belongs to an operation, not to a window, and Windows and macOS
    // have no window in the request. Nothing here acquires a lease or connects to a session bus.
    [Test, NotInParallel(Order = 32)]
    public async Task PlatformServicesRegisterOneInhibitionForTheApplication()
    {
        await using (var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            var inhibition = provider.GetRequiredService<IDesktopInhibition>();
            await using (var first = provider.CreateAsyncScope())
                Require(ReferenceEquals(inhibition, first.ServiceProvider.GetRequiredService<IDesktopInhibition>()),
                    "A Window scope got its own inhibition.");
            var expected = OperatingSystem.IsLinux() ? "Runic.Platform.Linux.Portal.PortalInhibition"
                : OperatingSystem.IsWindows() ? "Runic.Platform.Windows.WindowsDesktopInhibition"
                : OperatingSystem.IsMacOS() ? "Runic.Platform.MacOS.MacDesktopInhibition" : null;
            if (expected is not null)
                Require(inhibition.GetType().FullName == expected
                    && inhibition.SupportedEffects == (DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep),
                    $"The operating system's inhibition provider was not registered: {inhibition.GetType().FullName}");
        }

        var custom = DesktopInhibition.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        await using var kept = new ServiceCollection().AddSingleton(custom).AddRunicPlatformServices().BuildServiceProvider();
        Require(ReferenceEquals(kept.GetRequiredService<IDesktopInhibition>(), custom),
            "AddRunicPlatformServices replaced the application's inhibition.");
    }

    // One call registers every platform service a ViewModel can take. Each public interface of Runic.Platform is
    // classified here, so a new service contract fails this check until it is registered or listed as composed by
    // the application.
    [Test, NotInParallel(Order = 32)]
    public async Task PlatformServicesRegisterEveryPlatformService()
    {
        Type[] window = [typeof(IWindowPlatformServices), typeof(IFileDialogs), typeof(ITextClipboard), typeof(IDesktopClipboard), typeof(IDesktopFileLauncher),
            typeof(IDesktopMonitors), typeof(IDesktopMessageBoxes)];
        Type[] application = [typeof(IDesktopSettings), typeof(IDesktopNotifications), typeof(IDesktopStatusIcon), typeof(IDesktopGlobalShortcuts),
            typeof(IDesktopLoginStartup), typeof(IDesktopInhibition), typeof(IDesktopSecretStore), typeof(IDesktopTrash), typeof(IDesktopFileTransfers),
            typeof(IDesktopVolumes), typeof(IDesktopNetworkStatus), typeof(IDesktopApplications), typeof(IDesktopThumbnails), typeof(IDesktopFileBookmarks),
            typeof(IApplicationPlatformServices)];
        // Registration names the application's entries, so only the overload with an application ID registers it.
        Type[] withApplicationId = [typeof(IDesktopOpenRegistration)];
        // Privileged actions need declared actions and a provider package; the instance claim runs before any host, and
        // open requests need the instance and, on macOS, must exist before the first window.
        Type[] composed = [typeof(IDesktopPrivilegedActions), typeof(IApplicationInstance), typeof(IDesktopOpenRequests)];
        Type[] notServices = [typeof(IPlatformCapabilities), typeof(IDesktopServiceStatus), typeof(IDirectoryLease), typeof(IReadFileLease),
            typeof(IReadFileLeaseList), typeof(IDirectoryLeaseList), typeof(ISaveFileLease), typeof(IDocumentFileLease), typeof(ILaunchableFileLease), typeof(IFileWriteTransaction), typeof(IDesktopInhibitionLease),
            typeof(IApplicationLaunchReply), typeof(IForwardedLaunchReply), typeof(IDesktopBookmarkAccess)];
        var contracts = typeof(IFileDialogs).Assembly.GetExportedTypes().Where(static type => type.IsInterface).ToHashSet();
        var unclassified = contracts.Except(window).Except(application).Except(withApplicationId).Except(composed).Except(notServices).Select(static type => type.Name).ToArray();
        Require(unclassified.Length == 0, $"Classify these Runic.Platform interfaces for AddRunicPlatformServices: {string.Join(", ", unclassified)}");

        await using var provider = new ServiceCollection().AddRunicPlatformServices()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        foreach (var type in application)
            Require(ReferenceEquals(provider.GetService(type), scope.ServiceProvider.GetService(type)) && provider.GetService(type) is not null,
                $"{type.Name} is not one application service.");
        foreach (var type in window)
            Require(scope.ServiceProvider.GetService(type) is IPlatformCapabilities or IFileDialogs or ITextClipboard or IDesktopFileLauncher
                or IDesktopMonitors or IDesktopMessageBoxes,
                $"{type.Name} is not registered per window.");
        var unbound = scope.ServiceProvider.GetRequiredService<IWindowPlatformServices>();
        Require(unbound.GetSnapshot().Statuses.Values.All(static status => status is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)),
            "Window services outside a window did not report the missing native owner.");
        foreach (var type in composed.Concat(withApplicationId))
            Require(provider.GetService(type) is null, $"{type.Name} was registered without the application composing it.");
        await using var identified = new ServiceCollection().AddRunicPlatformServices("org.example.Notes")
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        foreach (var type in withApplicationId)
            Require(identified.GetService(type) is not null, $"{type.Name} is not registered with an application ID.");
    }

    // Linux and Windows pass opened files and URLs as arguments; macOS's Apple events are the native job's.
    [Test]
    public async Task OpenRequestsComeFromTheLaunchArguments()
    {
        if (OperatingSystem.IsMacOS()) return;
        await using var opens = DesktopPlatformServices.CreateOpenRequests(new(["org.example.notes"]), launch: new ApplicationLaunch(["--new", "org.example.notes:draft/1"], "/"));
        var opened = new TaskCompletionSource<DesktopOpenRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        opens.Opened += (_, request) => opened.TrySetResult(request);
        var request = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(request.Items is [{ Scheme: "org.example.notes" }], $"The launch's registered URL was not delivered: {string.Join(", ", request.Items)}");
        Require(Catch(() => DesktopPlatformServices.CreateOpenRequests(new(["not a scheme"]))) is ArgumentException, "An invalid scheme was accepted.");
    }

    // The activation wrappers off their platforms: the launch is the arguments, D-Bus activation is Linux's, and
    // bookmarks outside macOS are paths.
    [Test]
    public async Task ActivationWrappersSelectTheProvider()
    {
        var launch = DesktopPlatformServices.GetLaunch(["--new", "org.example.notes:draft"]);
        Require(launch.Arguments.SequenceEqual(["--new", "org.example.notes:draft"]) && launch.WorkingDirectory == Environment.CurrentDirectory, "GetLaunch is the process launch.");
        await using var instance = new NullInstance();
        if (!OperatingSystem.IsLinux())
            Require(await DesktopPlatformServices.ExportApplicationAsync("org.example.Notes", instance) is PlatformResult<IAsyncDisposable>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
                "D-Bus activation is unavailable off Linux.");
        Require(Catch(() => DesktopPlatformServices.CreateOpenRegistration(" ")) is ArgumentException, "An empty application ID was accepted.");
        if (OperatingSystem.IsMacOS()) return;
        string file = Path.GetTempFileName();
        try
        {
            var bookmarks = DesktopPlatformServices.CreateFileBookmarks();
            Require((await bookmarks.CreateAsync(file)).TryGetValue(out var bookmark) && (await bookmarks.ResolveAsync(bookmark)).TryGetValue(out var access)
                && access.LocalPath == file, "A path bookmark resolves to its file.");
            Require((await bookmarks.OpenDocumentAsync(bookmark)).TryGetValue(out var document) && document.LocalPath == file,
                "A path bookmark opens its file as a document.");
            await document!.DisposeAsync();
        }
        finally { File.Delete(file); }
    }

    private sealed class NullInstance : IApplicationInstance
    {
        public event EventHandler<ApplicationLaunch>? Launched { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task UnavailableInhibitionReportsTheReason()
    {
        var inhibition = DesktopInhibition.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Require(inhibition.SupportedEffects == DesktopInhibitionEffects.None, "Unavailable inhibition reported effects.");
        Require(await inhibition.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Exporting") is
            PlatformResult<IDesktopInhibitionLease>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
            "Unavailable inhibition did not report the provider unconfigured.");
        // Arguments are checked as a provider checks them, so a call wrong on one platform is wrong on every platform.
        Require(await CatchAsync(() => inhibition.AcquireAsync(DesktopInhibitionEffects.None, "Exporting").AsTask()) is ArgumentOutOfRangeException
            && await CatchAsync(() => inhibition.AcquireAsync((DesktopInhibitionEffects)4, "Exporting").AsTask()) is ArgumentOutOfRangeException
            && await CatchAsync(() => inhibition.AcquireAsync(DesktopInhibitionEffects.DisplaySleep, " ").AsTask()) is ArgumentException
            && await CatchAsync(() => inhibition.AcquireAsync(DesktopInhibitionEffects.DisplaySleep, new string('x', 129)).AsTask()) is ArgumentException,
            "Unavailable inhibition accepted a request a provider refuses.");
        using (var cancelled = new CancellationTokenSource())
        {
            await cancelled.CancelAsync();
            Require(await CatchAsync(() => inhibition.AcquireAsync(DesktopInhibitionEffects.SystemSleep, "Exporting", cancelled.Token).AsTask()) is OperationCanceledException,
                "A cancelled acquisition did not throw.");
        }
        Require(Catch(() => DesktopInhibition.Unavailable((PlatformUnavailableReason)99)) is ArgumentOutOfRangeException,
            "An undefined reason was accepted.");
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
    // I-023: the applicationId overloads pass the ID to the provider and check it as AddRunicPlatformServices does.
    [Test]
    public void SecretStoreAndApplicationDatabaseTakeAnApplicationId()
    {
        var store = DesktopPlatformServices.CreateSecretStore("org.example.Notes");
        var applications = DesktopPlatformServices.CreateApplications("org.example.Notes");
        Require(store.GetType() == DesktopPlatformServices.CreateSecretStore().GetType()
            && applications.GetType() == DesktopPlatformServices.CreateApplications().GetType(),
            $"The overloads created other providers: {store.GetType().FullName}, {applications.GetType().FullName}.");
        if (OperatingSystem.IsLinux())
        {
            Require(PortalIdentity(store) == "org.example.Notes" && PortalIdentity(applications) == "org.example.Notes",
                "The Linux secret store or application database did not use the application ID.");
            Require(PortalIdentity(DesktopPlatformServices.CreateSecretStore()) is null
                && PortalIdentity(DesktopPlatformServices.CreateApplications()) is null,
                "The overloads without an ID did not leave the identity to the portal.");
            Require(Catch(() => DesktopPlatformServices.CreateSecretStore("Notes")) is ArgumentException
                && Catch(() => DesktopPlatformServices.CreateApplications("Notes")) is ArgumentException,
                "Linux accepted an application ID that is not a reverse-DNS desktop ID.");
        }
        Require(Catch(() => DesktopPlatformServices.CreateSecretStore(" ")) is ArgumentException
            && Catch(() => DesktopPlatformServices.CreateApplications("")) is ArgumentException
            && Catch(() => DesktopPlatformServices.CreateSecretStore((string)null!)) is ArgumentNullException,
            "An empty application ID was accepted.");
    }

    // B15: the options overload creates the same provider, with or without an application ID, and checks its arguments.
    [Test]
    public void SecretStoreTakesOptions()
    {
        var options = new DesktopSecretStoreOptions { AllowUserInteraction = false, RequireEncryption = true };
        var store = DesktopPlatformServices.CreateSecretStore("org.example.Notes", options);
        Require(store.GetType() == DesktopPlatformServices.CreateSecretStore().GetType()
            && DesktopPlatformServices.CreateSecretStore(null, options).GetType() == store.GetType(),
            $"The options overload created another provider: {store.GetType().FullName}.");
        if (OperatingSystem.IsLinux())
            Require(PortalIdentity(store) == "org.example.Notes" && PortalIdentity(DesktopPlatformServices.CreateSecretStore(null, options)) is null,
                "The Linux options overload did not pass the application ID.");
        Require(Catch(() => DesktopPlatformServices.CreateSecretStore("org.example.Notes", null!)) is ArgumentNullException
            && Catch(() => DesktopPlatformServices.CreateSecretStore("", options)) is ArgumentException,
            "The options overload accepted invalid arguments.");
    }

    // The ApplicationId of the PortalApplication a Linux provider keeps.
    private static string? PortalIdentity(object provider)
    {
        foreach (var field in provider.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            if (field.FieldType.FullName == "Runic.Platform.Linux.Portal.PortalApplication")
                return field.GetValue(provider)?.GetType().GetProperty("ApplicationId")?.GetValue(field.GetValue(provider)) as string;
        }
        throw new InvalidOperationException($"{provider.GetType().FullName} keeps no PortalApplication.");
    }

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
