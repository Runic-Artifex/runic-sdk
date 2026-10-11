using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Platform;

// Checks for the platform-services facade: provider selection from the running backend, and per-window services a
// ViewModel takes in its constructor that bind when the window opens and stop when it closes.
static class PlatformServicesChecks
{
    internal static async Task RunAsync()
    {
        ProviderFollowsTheRunningBackend();
        await ViewModelServicesBindWhenTheWindowOpens();
        await WindowWithoutNativeDispatchReportsNoOwner();
        await ForWindowBindsAnOpenWindow();
        await ReadmeSampleReportsAnUnboundWindow();
        // Claims GTK 4 for this process, so it runs last.
        if (OperatingSystem.IsLinux()) await Gtk4ProcessSelectsOnlyTheGtk4Provider();
    }

    private static void ProviderFollowsTheRunningBackend()
    {
        Require(PlatformServices.Select(windows: true, macOS: false, linux: false, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.Windows,
            "Windows did not select the Windows provider.");
        Require(PlatformServices.Select(windows: false, macOS: true, linux: false, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.MacOS,
            "macOS did not select the AppKit provider.");
        Require(PlatformServices.Select(windows: false, macOS: false, linux: true, gtk3Claimed: true, gtk4Claimed: false) == DesktopPlatformProvider.LinuxGtk3,
            "A GTK 3 process did not select the GTK 3 provider.");
        Require(PlatformServices.Select(windows: false, macOS: false, linux: true, gtk3Claimed: false, gtk4Claimed: true) == DesktopPlatformProvider.LinuxGtk4,
            "A GTK 4 process did not select the GTK 4 provider.");
        Require(PlatformServices.Select(windows: false, macOS: false, linux: true, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.None,
            "A Linux process without a toolkit selected a provider.");
        Require(PlatformServices.Select(windows: false, macOS: false, linux: false, gtk3Claimed: false, gtk4Claimed: false) == DesktopPlatformProvider.None,
            "An unknown operating system selected a provider.");
    }

    private static async Task ViewModelServicesBindWhenTheWindowOpens()
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
            Require(services.IsBound && services.Provider == PlatformServices.CurrentProvider,
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
        Require(viewModel.Platform.GetSnapshot().Statuses.Values.All(static status => status is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed)),
            "Capabilities of a closed window were not all owner-closed.");
    }

    private static async Task WindowWithoutNativeDispatchReportsNoOwner()
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
            && await services.FileLauncher.LaunchAsync(Path.Combine(Path.GetTempPath(), "notes.txt")) is PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A window without native dispatch did not report the owner unavailable.");
    }

    private static async Task ForWindowBindsAnOpenWindow()
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = new NativeDispatchFactory(), WaitForConnection = false });
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html("<!doctype html>") });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded });
        var services = PlatformServices.ForWindow(new DesktopNativeOwner(window));
        Require(services.IsBound && services.Provider == PlatformServices.CurrentProvider, "ForWindow did not bind the open window.");
        await services.DisposeAsync();
        await services.DisposeAsync();
        Require(await services.Clipboard.ReadTextAsync(16) is PlatformResult<string?>.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "Disposed window services did not report the owner closed.");
    }

    private static async Task ReadmeSampleReportsAnUnboundWindow()
    {
        // Before the window opens nothing is selected, read or saved.
        await using var provider = new ServiceCollection().AddRunicPlatformServices().BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var notes = new ReadmeNotesViewModel(scope.ServiceProvider.GetRequiredService<IFileDialogs>()) { Text = "unchanged" };
        await notes.OpenAsync();
        Require(notes.Text == "unchanged" && !await notes.SaveAsync(), "The README sample read or saved without a window.");
    }

    private static async Task Gtk4ProcessSelectsOnlyTheGtk4Provider()
    {
        LinuxDesktopRuntime.ClaimBackend(LinuxEmbeddedBackend.Gtk4WebKit6);
        Require(PlatformServices.CurrentProvider == DesktopPlatformProvider.LinuxGtk4, "A GTK 4 process did not select the GTK 4 provider.");
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
        if (await files.OpenFileAsync(new()) is not PickerResult<IReadFileLease>.Selected(var lease)) return;
        await using (lease)
        await using (var stream = await lease.OpenReadAsync())
        using (var reader = new StreamReader(stream))
            Text = await reader.ReadToEndAsync();
    }

    public async Task<bool> SaveAsync()
    {
        if (await files.SaveFileAsync(new("notes.txt")) is not PickerResult<ISaveFileLease>.Selected(var lease)) return false;
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
