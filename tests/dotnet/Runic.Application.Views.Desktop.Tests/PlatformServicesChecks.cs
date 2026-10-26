using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;
using TUnit.Core;
using TUnit.Core.Enums;

// Checks for the platform-services facade: provider selection from the running backend, and per-window services a
// ViewModel takes in its constructor that bind when the window opens and stop when it closes.
internal sealed class PlatformServicesChecks
{
    [Test, NotInParallel(Order = 13)]
    public void ProviderFollowsTheRunningBackend()
    {
        Require(DesktopPlatformServices.Select(windows: true, macOS: false, linux: false, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.Windows,
            "Windows did not select the Windows provider.");
        Require(DesktopPlatformServices.Select(windows: false, macOS: true, linux: false, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.MacOS,
            "macOS did not select the AppKit provider.");
        Require(DesktopPlatformServices.Select(windows: false, macOS: false, linux: true, gtk3Claimed: true, gtk4Claimed: false) == DesktopPlatformProvider.LinuxGtk3,
            "A GTK 3 process did not select the GTK 3 provider.");
        Require(DesktopPlatformServices.Select(windows: false, macOS: false, linux: true, gtk3Claimed: false, gtk4Claimed: true) == DesktopPlatformProvider.LinuxGtk4,
            "A GTK 4 process did not select the GTK 4 provider.");
        Require(DesktopPlatformServices.Select(windows: false, macOS: false, linux: true, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.None,
            "A Linux process without a toolkit selected a provider.");
        Require(DesktopPlatformServices.Select(windows: false, macOS: false, linux: false, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.None,
            "An unknown operating system selected a provider.");
    }

    [Test, NotInParallel(Order = 14)]
    public async Task ViewModelServicesBindWhenTheWindowOpens()
    {
        await using var provider = CreateProvider();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
        var viewModel = window.ViewModel;
        try
        {
            var services = (DesktopWindowPlatformServices)viewModel.Platform;
            Require(viewModel.BeforeOpen is PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                "A file dialog before the window opened did not report the owner unavailable.");
            Require(viewModel.SnapshotBeforeOpen.Statuses.Values.All(static status => status is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)),
                "Capabilities before the window opened were not all owner-unavailable.");
            Require(ReferenceEquals(viewModel.Files, services.FileDialogs) && ReferenceEquals(viewModel.Clipboard, services.Clipboard),
                "The window scope resolved more than one set of platform services.");
            Require(services.IsBound && services.Provider == DesktopPlatformServices.CurrentProvider,
                "Opening the window did not bind its services with the current provider.");
            var files = services.GetSnapshot().Statuses["platform.files.open"];
            if (services.Provider == DesktopPlatformProvider.None)
            {
                // Linux test processes run no toolkit, so the window is native but no provider matches it.
                Require(files is CapabilityStatus.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "A window whose toolkit has no provider did not report the provider unconfigured.");
                Require(await viewModel.Files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.ProviderNotConfigured),
                    "A file dialog without a provider did not report the provider unconfigured.");
            }
            else
            {
                Require(files is CapabilityStatus.Available && services.GetSnapshot().Statuses["platform.clipboard.readText"] is CapabilityStatus.Available,
                    "The bound window did not report its native services available.");
            }

            // A second window has its own scope and its own services.
            await using (var second = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>())
            {
                Require(!ReferenceEquals(second.DataContext!.Platform, viewModel.Platform), "Two windows shared their platform services.");
            }
            Require(services.IsBound && services.GetSnapshot().Statuses["platform.files.open"] is not CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed),
                "Closing another window stopped this window's services.");
        }
        finally
        {
            await window.DisposeAsync();
        }
        Require(await viewModel.Files.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && await viewModel.Clipboard.WriteTextAsync("text") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "Services of a closed window did not report the owner closed.");
        // The window's clipboard is also its desktop clipboard, and the badge follows the window too.
        Require(viewModel.Clipboard is IDesktopClipboard desktopClipboard
            && await desktopClipboard.ReadImageAsync(16) is PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && await desktopClipboard.WriteAsync(new() { Html = "<b>x</b>" }) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && await viewModel.Platform.SetBadgeAsync(new() { Count = 1 }) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && await viewModel.Platform.SetProgressAsync(null) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The desktop clipboard and taskbar of a closed window did not report the owner closed.");
        Require(viewModel.Platform.GetSnapshot().Statuses.Values.All(static status => status is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)),
            "Capabilities of a closed window were not all owner-closed.");
    }

    [Test, NotInParallel(Order = 15)]
    public async Task WindowWithoutNativeDispatchReportsNoOwner()
    {
        await using var provider = CreateProvider();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory { SupportsDispatch = false },
            WaitForConnection = false,
        });
        await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
        var services = (DesktopWindowPlatformServices)window.DataContext!.Platform;
        Require(services.IsBound && services.Provider == DesktopPlatformProvider.None,
            "A window without native dispatch selected a provider.");
        Require(await services.FileDialogs.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && await services.FileDialogs.OpenDocumentAsync(new()) is PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && await services.FileDialogs.SaveDocumentAsync(new("notes.txt")) is PickerResult<IDocumentFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && await services.FileLauncher.LaunchAsync(Path.Combine(Path.GetTempPath(), "notes.txt")) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A window without native dispatch did not report the owner unavailable.");
    }

    [Test, NotInParallel(Order = 16)]
    public async Task ForWindowBindsAnOpenWindow()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var services = DesktopPlatformServices.ForWindow(new DesktopNativeOwner(window));
        Require(services.IsBound && services.Provider == DesktopPlatformServices.CurrentProvider, "ForWindow did not bind the open window.");
        await services.DisposeAsync();
        await services.DisposeAsync();
        Require(await services.Clipboard.ReadTextAsync(16) is PlatformResult<string?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "Disposed window services did not report the owner closed.");
    }

    [Test, NotInParallel(Order = 16)]
    public async Task PresentReportsWindowsItCannotRaise()
    {
        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory { SupportsDispatch = false },
            WaitForConnection = false,
        }))
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            Require(await DesktopPlatformServices.PresentAsync(new DesktopNativeOwner(window)) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
                "A window without native dispatch did not report the owner unavailable.");
        }

        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false }))
        await using (var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") }))
        {
            var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
            var owner = new DesktopNativeOwner(window);
            await window.CloseAsync();
            Require(await DesktopPlatformServices.PresentAsync(owner) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                "A closed window did not report the owner closed.");
        }

        // A Runic Window is presented through its Desktop window's owner: the closed presentation reports it.
        await using (var provider = CreateProvider())
        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false }))
        {
            await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            await ((DesktopBridgeWindow<FileViewModel>)window.Host).Window.CloseAsync();
            Require(await DesktopPlatformServices.PresentAsync(window) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                "A Runic Window whose presentation closed did not report the owner closed.");
        }
    }

    // #318: a Window announces through its embedded window, from the static facade and from the services a ViewModel
    // holds; a window that cannot, or none, reports why.
    [Test, NotInParallel(Order = 16)]
    public async Task AnnouncementsReachTheEmbeddedWindowOrReportWhyNot()
    {
        await using (var provider = CreateProvider())
        {
            var factory = new NativeDispatchFactory { Announces = true };
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
            var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            var services = (DesktopWindowPlatformServices)window.ViewModel.Platform;
            try
            {
                Require(await DesktopPlatformServices.AnnounceAsync(window, "Saved") is PlatformResult<PlatformUnit>.Success,
                    "A Window did not announce through its embedded window.");
                Require(await services.AnnounceAsync("The disk is full", DesktopAnnouncementPriority.Assertive) is PlatformResult<PlatformUnit>.Success,
                    "The ViewModel's platform services did not announce.");
                // The host-neutral member a ViewModel reaches through IWindowPlatformServices.
                Require(await window.ViewModel.Platform.AnnounceAsync("Copied") is PlatformResult<PlatformUnit>.Success
                    && await window.ViewModel.Platform.AnnounceAsync("Offline", PlatformAnnouncementPriority.Assertive) is PlatformResult<PlatformUnit>.Success,
                    "IWindowPlatformServices.AnnounceAsync did not announce through the embedded window.");
                Require(factory.Hosts[0].Announcements.SequenceEqual(
                    [("Saved", DesktopAnnouncementPriority.Polite), ("The disk is full", DesktopAnnouncementPriority.Assertive),
                     ("Copied", DesktopAnnouncementPriority.Polite), ("Offline", DesktopAnnouncementPriority.Assertive)]),
                    "The embedded window did not receive the announcements with their priorities.");
                await ((DesktopBridgeWindow<FileViewModel>)window.Host).Window.CloseAsync();
                Require(await DesktopPlatformServices.AnnounceAsync(window, "Late") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                    "A Window whose presentation closed did not report the owner closed.");
            }
            finally
            {
                await window.DisposeAsync();
            }
            Require(await services.AnnounceAsync("Late") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                "The services of a closed window did not report the owner closed.");
            Require(await ((IWindowPlatformServices)services).AnnounceAsync("Late") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                "IWindowPlatformServices.AnnounceAsync of a closed window did not report the owner closed.");
        }

        await using (var provider = CreateProvider())
        {
            var factory = new NativeDispatchFactory();
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = factory, WaitForConnection = false });
            await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            Require(await DesktopPlatformServices.AnnounceAsync(window, "Saved") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "A window host without announcements did not report the backend unavailable.");
            Require(factory.Hosts[0].Announcements.IsEmpty, "A window host without announcements received one.");
        }

        var unbound = new DesktopWindowPlatformServices();
        IWindowPlatformServices neutral = unbound;
        Require(await unbound.AnnounceAsync("Saved") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "Services of a window that has not opened did not report the owner unavailable.");
        Require(await neutral.AnnounceAsync("Saved", PlatformAnnouncementPriority.Assertive) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "IWindowPlatformServices.AnnounceAsync of a window that has not opened did not report the owner unavailable.");
        var rejected = false;
        try { _ = await unbound.AnnounceAsync(""); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "An empty announcement was accepted.");
        rejected = false;
        try { _ = await neutral.AnnounceAsync(" "); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, "An empty announcement was accepted through IWindowPlatformServices.");
        rejected = false;
        try { _ = await neutral.AnnounceAsync("Saved", (PlatformAnnouncementPriority)7); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected, "An undefined announcement priority was accepted through IWindowPlatformServices.");
        await unbound.DisposeAsync();
        Require(await neutral.AnnounceAsync("Late") is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "IWindowPlatformServices.AnnounceAsync of stopped services did not report the owner closed.");
    }

    // B10: a ViewModel shows a native context menu through IWindowPlatformServices and gets the chosen item; a window
    // without context menus, or none, reports why, and the snapshot reports both menu capabilities.
    [Test, NotInParallel(Order = 17)]
    public async Task ContextMenusReachTheEmbeddedWindowOrReportWhyNot()
    {
        System.Collections.Immutable.ImmutableArray<DesktopMenuEntry> menu =
            [new DesktopMenuItem("Rename", "row.rename") { CommandParameter = "row-7" }, new DesktopMenuSeparator()];
        await using (var provider = CreateProvider())
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory { ContextMenus = true }, WaitForConnection = false,
            });
            var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            var services = window.ViewModel.Platform;
            Require(window.ViewModel.SnapshotBeforeOpen.Statuses[PlatformCapabilityIds.ContextMenu]
                is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "The context menu capability was available before the window opened.");
            try
            {
                Require(await services.ShowContextMenuAsync(menu, new DesktopContextMenuOptions { X = 12, Y = 34 })
                    is PlatformResult<DesktopMenuItem?>.Success(var chosen) && ReferenceEquals(chosen, menu[0]),
                    "IWindowPlatformServices.ShowContextMenuAsync did not return the item the window's menu chose.");
                var statuses = services.GetSnapshot().Statuses;
                Require(statuses[PlatformCapabilityIds.ContextMenu] is CapabilityStatus.Available
                    && statuses[PlatformCapabilityIds.MenuBar] is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                    "The snapshot did not report context menus and no native menu bar.");
                await ((DesktopBridgeWindow<FileViewModel>)window.Host).Window.CloseAsync();
                Require(await services.ShowContextMenuAsync(menu) is PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                    "The context menu of a closed window did not report the owner closed.");
            }
            finally
            {
                await window.DisposeAsync();
            }
        }

        await using (var provider = CreateProvider())
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
            await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            Require(await window.ViewModel.Platform.ShowContextMenuAsync(menu) is PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "A window host without context menus did not report the backend unavailable.");
            var rejected = false;
            try { _ = await window.ViewModel.Platform.ShowContextMenuAsync([]); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "An empty context menu was accepted.");
        }

        IWindowPlatformServices unbound = new DesktopWindowPlatformServices();
        Require(await unbound.ShowContextMenuAsync(menu) is PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "The context menu of a window that has not opened did not report the owner unavailable.");
        Require(unbound.GetSnapshot().Statuses[PlatformCapabilityIds.MenuBar] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "The menu bar capability of a window that has not opened was not unavailable.");
        await ((DesktopWindowPlatformServices)unbound).DisposeAsync();
        Require(await unbound.ShowContextMenuAsync(menu) is PlatformResult<DesktopMenuItem?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The context menu of stopped services did not report the owner closed.");
    }

    // B11: a ViewModel starts a drag out of the embedded window through IWindowPlatformServices and gets the effect; a
    // window without drags, or none, reports why, and the snapshot reports the drag capability.
    [Test, NotInParallel(Order = 17)]
    public async Task DragsReachTheEmbeddedWindowOrReportWhyNot()
    {
        var data = new DesktopDragData { Text = "row 7" };
        await using (var provider = CreateProvider())
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions
            {
                WindowHostFactory = new NativeDispatchFactory { Drags = true }, WaitForConnection = false,
            });
            var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            var services = window.ViewModel.Platform;
            Require(window.ViewModel.SnapshotBeforeOpen.Statuses[PlatformCapabilityIds.DragOut]
                is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable), "The drag capability was available before the window opened.");
            try
            {
                Require(await services.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Success(DesktopDragEffects.Copy),
                    "IWindowPlatformServices.StartDragAsync did not return the window's effect.");
                Require(services.GetSnapshot().Statuses[PlatformCapabilityIds.DragOut] is CapabilityStatus.Available,
                    "The snapshot did not report drags.");
                await ((DesktopBridgeWindow<FileViewModel>)window.Host).Window.CloseAsync();
                Require(await services.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.OwnerClosed),
                    "The drag of a closed window did not report the owner closed.");
            }
            finally
            {
                await window.DisposeAsync();
            }
        }

        await using (var provider = CreateProvider())
        {
            await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
            await using var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>();
            Require(await window.ViewModel.Platform.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "A window host without drags did not report the backend unavailable.");
            Require(window.ViewModel.Platform.GetSnapshot().Statuses[PlatformCapabilityIds.DragOut]
                is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable), "The snapshot reported drags for a window without them.");
            var rejected = false;
            try { _ = await window.ViewModel.Platform.StartDragAsync(new DesktopDragData()); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "An empty drag was accepted.");
        }

        IWindowPlatformServices unbound = new DesktopWindowPlatformServices();
        Require(await unbound.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "The drag of a window that has not opened did not report the owner unavailable.");
        await ((DesktopWindowPlatformServices)unbound).DisposeAsync();
        Require(await unbound.StartDragAsync(data) is PlatformResult<DesktopDragEffects>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The drag of stopped services did not report the owner closed.");
    }

    // #313: a Window opened for a forwarded launch is presented with the launch's activation context as soon as it is
    // shown, so that the compositor gives it focus. Without a context, for a hidden window and for a window without
    // native dispatch nothing is presented, and a presentation that fails does not fail the open.
    [Test, NotInParallel(Order = 16)]
    public async Task OpenWindowWithActivationPresentsTheNewWindow()
    {
        await using var provider = CreateProvider();
        var activation = new DesktopActivationContext(activationToken: "launch-token");
        List<(DesktopNativeOwner Owner, DesktopActivationContext? Context)> presented = [];
        var fail = false;
        ValueTask<PlatformResult<PlatformUnit>> Record(DesktopNativeOwner owner, DesktopActivationContext? context, CancellationToken _)
        {
            presented.Add((owner, context));
            if (fail) throw new InvalidOperationException("The desktop refused.");
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(new PlatformUnit()));
        }
        RunicDesktopHost Windows(DesktopHost desktop, bool hidden = false) => new(provider, desktop, new RunicDesktopHostOptions
        {
            SurfaceOptions = new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") },
            WindowOptions = new DesktopWindowOptions { Browser = BrowserKind.Embedded, Hidden = hidden },
        })
        {
            Present = Record,
        };

        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false }))
        {
            await using (var window = await Windows(host).OpenWindowAsync<FileWindow>(null, activation))
            {
                var owner = ((DesktopBridgeWindow<FileViewModel>)window.Host).NativeOwner;
                Require(presented.Count == 1 && ReferenceEquals(presented[0].Owner, owner) && ReferenceEquals(presented[0].Context, activation),
                    "The new window was not presented once with the launch's activation context.");
            }
            await using (await Windows(host).OpenWindowAsync<FileWindow>(options: null, activation: null)) { }
            await using (await Windows(host, hidden: true).OpenWindowAsync<FileWindow>(null, activation)) { }
            Require(presented.Count == 1, "A window without a context, or a hidden one, was presented.");

            fail = true;
            await using (var refused = await Windows(host).OpenWindowAsync<FileWindow>(null, activation))
            {
                Require(presented.Count == 2 && refused.Host is DesktopBridgeWindow<FileViewModel> { Window.IsOpen: true },
                    "A refused presentation failed the open or closed the window.");
            }
        }

        await using (var host = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = new NativeDispatchFactory { SupportsDispatch = false },
            WaitForConnection = false,
        }))
        await using (await Windows(host).OpenWindowAsync<FileWindow>(null, activation)) { }
        Require(presented.Count == 2, "A window without native dispatch was presented.");
    }

    [Test, NotInParallel(Order = 17)]
    public async Task ReadmeSampleReportsAnUnboundWindow()
    {
        // Before the window opens nothing is selected, read or saved.
        await using var provider = new ServiceCollection().AddRunicPlatformServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var notes = new ReadmeNotesViewModel(scope.ServiceProvider.GetRequiredService<IFileDialogs>()) { Text = "unchanged" };
        await notes.OpenAsync();
        Require(notes.Text == "unchanged" && !await notes.SaveAsync(), "The README sample read or saved without a window.");
        var editor = new ReadmeEditorViewModel(scope.ServiceProvider.GetRequiredService<IFileDialogs>()) { Text = "unchanged" };
        await editor.OpenAsync();
        Require(editor.Text == "unchanged" && await editor.SaveAsync() is null, "The README document sample read or saved without a window.");
    }

    // Claims GTK 4 for this process, so it runs last.
    [Test, NotInParallel(Order = 20), RunOn(OS.Linux)]
    public async Task Gtk4ProcessSelectsOnlyTheGtk4Provider()
    {
        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk4WebKit6);
        Require(DesktopPlatformServices.CurrentProvider == DesktopPlatformProvider.LinuxGtk4, "A GTK 4 process did not select the GTK 4 provider.");
        await using var provider = CreateProvider();
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        await using (var window = await TestWindows.EmbeddedHtml(provider, host).OpenWindowAsync<FileWindow>())
        {
            var services = (DesktopWindowPlatformServices)window.DataContext!.Platform;
            Require(services.Provider == DesktopPlatformProvider.LinuxGtk4 && services.GetSnapshot().Statuses["platform.files.open"] is CapabilityStatus.Available,
                "The GTK 4 window did not bind the GTK 4 provider.");
        }
        Require(!AppDomain.CurrentDomain.GetAssemblies().Any(static assembly => assembly.GetName().Name == "Runic.Platform.Linux"),
            "A GTK 4 process loaded the GTK 3 provider assembly.");
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
}

// Takes its services in the constructor, before its window opens.
sealed class FileViewModel : INotifyPropertyChanged
{
    public FileViewModel(IFileDialogs files, ITextClipboard clipboard, IWindowPlatformServices platform)
    {
        Files = files;
        Clipboard = clipboard;
        Platform = platform;
        BeforeOpen = files.OpenFileAsync(new()).AsTask().GetAwaiter().GetResult();
        SnapshotBeforeOpen = platform.GetSnapshot();
    }

    public IFileDialogs Files { get; }
    public ITextClipboard Clipboard { get; }
    public IWindowPlatformServices Platform { get; }
    public PickerResult<IReadFileLease> BeforeOpen { get; }
    public CapabilitySnapshot SnapshotBeforeOpen { get; }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

sealed class FileWindow : RunicWindow<FileViewModel>;

sealed class NoopAttachment : IDisposable
{
    public void Dispose() { }
}

// The open/read/atomic-save sample in the package README, without the MVVM toolkit, so it keeps compiling.
sealed class ReadmeNotesViewModel(IFileDialogs files)
{
    public string Text { get; set; } = "";

    public async Task OpenAsync()
    {
        if (await files.OpenFileAsync(new() { Filters = [new("Notes", ["txt", "md"])] }) is not PickerResult<IReadFileLease>.Selected(var lease)) return;
        await using (lease)
        await using (var stream = await lease.OpenReadAsync())
        using (var reader = new StreamReader(stream))
            Text = await reader.ReadToEndAsync();
    }

    public async Task<bool> SaveAsync()
    {
        if (await files.SaveFileAsync(new("notes.txt") { Filters = [new("Notes", ["txt"])] }) is not PickerResult<ISaveFileLease>.Selected(var lease)) return false;
        await using (lease)
        {
            if (await lease.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)
                is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) return false;
            await using (transaction)
            {
                await using (var writer = new StreamWriter(transaction.Content, leaveOpen: true))
                    await writer.WriteAsync(Text);
                return await transaction.CommitAsync() is FileCommitResult.Committed;
            }
        }
    }
}

// The document sample in the package README, without the MVVM toolkit, so it keeps compiling.
sealed class ReadmeEditorViewModel(IFileDialogs files)
{
    private IDocumentFileLease? _document;
    public string Text { get; set; } = "";

    public async Task OpenAsync()
    {
        if (await files.OpenDocumentAsync(new() { Filters = [new("Notes", ["txt", "md"])] }) is not PickerResult<IDocumentFileLease>.Selected(var document)) return;
        if (_document is not null) await _document.DisposeAsync();
        _document = document;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        if (_document is null || await _document.OpenReadAsync() is not PlatformResult<Stream>.Success(var stream)) return;
        await using (stream)
        using (var reader = new StreamReader(stream))
            Text = await reader.ReadToEndAsync();
    }

    public async Task<FileCommitResult?> SaveAsync()
    {
        if (_document is null || await _document.BeginWriteAsync(FileWritePolicy.RequireAtomicReplace)
            is not PlatformResult<IFileWriteTransaction>.Success(var transaction)) return null;
        await using (transaction)
        {
            await using (var writer = new StreamWriter(transaction.Content, leaveOpen: true))
                await writer.WriteAsync(Text);
            return await transaction.CommitAsync();
        }
    }
}
