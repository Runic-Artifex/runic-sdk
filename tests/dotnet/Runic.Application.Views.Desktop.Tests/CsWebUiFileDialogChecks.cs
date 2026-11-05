extern alias cswebui;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Runic.Platform;
using Runic.Platform.Runtime;
using TUnit.Core;
using CsWebUiServices = cswebui::Runic.Application.Views.CsWebUi.CsWebUiPlatformServiceCollectionExtensions;
using WindowLocality = cswebui::Runic.Application.Views.CsWebUi.CsWebUiWindowLocality;

// CS-WebUI windows have no native owner. With a platform's unowned file dialogs (CreateUnownedFileDialogs of the Windows
// and macOS providers, or the portal's CreateUnparentedFileDialogs), each window's IFileDialogs shows them for
// OwnerPolicy.PreferOwner, as Runic Desktop does for a window without native dispatch (#712), while no other computer can
// reach the window (CsWebUiWindowLocality, which RunicCsWebUiHost attaches when it shows the window); the window's scope
// ends them.
internal sealed class CsWebUiFileDialogChecks
{
    [Test]
    public async Task UnownedDialogsServeEachWindowUntilItCloses()
    {
        var backend = new BlockingBackend();
        var services = new ServiceCollection();
        Require(ReferenceEquals(CsWebUiServices.AddRunicPlatformServices(services, backend), services), "The registration did not return the services.");
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var scope = provider.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<IWindowPlatformServices>();
        var dialogs = scope.ServiceProvider.GetRequiredService<IFileDialogs>();
        Require(ReferenceEquals(dialogs, platform.FileDialogs), "IFileDialogs is not the window services' dialogs.");

        // Until the host attaches the shown window, and while another computer can reach it, there is no dialog.
        Require(await dialogs.OpenDirectoryAsync(new(OwnerPolicy.PreferOwner)) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && platform.GetSnapshot().Statuses[PlatformCapabilityIds.FilesOpen] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && backend.Calls == 0,
            "A window the host had not shown asked for an unowned dialog.");
        var local = false;
        scope.ServiceProvider.GetRequiredService<WindowLocality>().Attach(() => local);
        Require(await dialogs.OpenDirectoryAsync(new(OwnerPolicy.PreferOwner)) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && backend.Calls == 0,
            "A window that another computer can reach asked for an unowned dialog.");
        local = true;
        await using (var other = provider.CreateAsyncScope())
            Require(!ReferenceEquals(other.ServiceProvider.GetRequiredService<IFileDialogs>(), dialogs), "Two windows share their dialogs.");

        var statuses = platform.GetSnapshot().Statuses;
        Require(statuses[PlatformCapabilityIds.DialogsUnowned] is CapabilityStatus.Available
            && statuses[PlatformCapabilityIds.DialogsOwned] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && statuses[PlatformCapabilityIds.FilesOpen] is CapabilityStatus.Available
            && statuses[PlatformCapabilityIds.DirectoriesOpen] is CapabilityStatus.Available
            && statuses[PlatformCapabilityIds.FilesOpenMultiple] is CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable)
            && statuses[PlatformCapabilityIds.ClipboardReadText] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            $"The window did not report unowned file dialogs: {string.Join(", ", statuses)}.");

        // RequireOwner, the options' default, still reports the missing owner without showing a dialog.
        Require(await dialogs.OpenDirectoryAsync(new()) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && await dialogs.OpenFileAsync(new()) is PickerResult<IReadFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && await dialogs.SaveFileAsync(new("notes.txt")) is PickerResult<ISaveFileLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && backend.Calls == 0,
            "A RequireOwner request did not report the owner unavailable.");
        // A backend without multiple selection reports it unavailable.
        Require(await dialogs.OpenFilesAsync(new(OwnerPolicy.PreferOwner)) is PickerResult<IReadFileLeaseList>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            "Multiple selection of a backend without it was not unavailable.");

        // PreferOwner shows the unowned dialog with the request's options; its lease belongs to the window.
        var options = new OpenDirectoryOptions(OwnerPolicy.PreferOwner) { Title = "Open workspace", InitialDirectory = Path.GetTempPath() };
        var pick = dialogs.OpenDirectoryAsync(options).AsTask();
        var shown = await backend.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(ReferenceEquals(shown, options), "The dialog did not receive the request's options.");
        // One dialog at a time per window.
        Require(await dialogs.OpenDirectoryAsync(options) is PickerResult<IDirectoryLease>.Failed(PlatformFailureCode.ResourceBusy, _),
            "A second dialog while one is shown was not busy.");
        backend.Answer.SetResult(new PickerResult<IDirectoryLease>.Selected(backend.Lease));
        var picked = await pick.WaitAsync(TimeSpan.FromSeconds(10));
        Require(picked is PickerResult<IDirectoryLease>.Selected(var lease) && lease.LocalPath == backend.Lease.LocalPath && !ReferenceEquals(lease, backend.Lease),
            $"The selection did not reach the caller as the window's lease: {picked}.");

        // Each request checks the window again: one that became public gets no dialog.
        local = false;
        Require(await dialogs.OpenDirectoryAsync(options) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable)
            && backend.Calls == 1
            && platform.GetSnapshot().Statuses[PlatformCapabilityIds.DialogsUnowned] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A window that became public asked for an unowned dialog.");
        local = true;

        // A dialog that is shown when the window closes closes with it.
        backend.Reset();
        var pending = dialogs.OpenDirectoryAsync(options).AsTask();
        await backend.Shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scope.DisposeAsync();
        Require(await pending.WaitAsync(TimeSpan.FromSeconds(10)) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && backend.Cancelled,
            "Closing the window did not close its dialog.");
        Require(backend.Lease.Disposed, "Closing the window did not release its lease.");
        Require(await dialogs.OpenDirectoryAsync(options) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            && platform.GetSnapshot().Statuses[PlatformCapabilityIds.DialogsUnowned] is CapabilityStatus.Unavailable(PlatformUnavailableReason.OwnerClosed),
            "The dialogs of a closed window did not report the owner closed.");
    }

    [Test]
    public async Task WithoutUnownedDialogsOrALocalWindowTheOwnerIsUnavailable()
    {
        var services = new ServiceCollection();
        CsWebUiServices.AddRunicPlatformServices(services);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dialogs = scope.ServiceProvider.GetRequiredService<IFileDialogs>();
        Require(await dialogs.OpenDirectoryAsync(new(OwnerPolicy.PreferOwner)) is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable),
            "A CS-WebUI window without unowned dialogs did not report the owner unavailable.");

        // A window whose locality fails, as one of a disposed WebUI window does, is not local.
        var backend = new BlockingBackend();
        var served = new ServiceCollection();
        CsWebUiServices.AddRunicPlatformServices(served, backend);
        await using var servedProvider = served.BuildServiceProvider();
        await using var servedScope = servedProvider.CreateAsyncScope();
        servedScope.ServiceProvider.GetRequiredService<WindowLocality>().Attach(static () => throw new ObjectDisposedException("window"));
        Require(await servedScope.ServiceProvider.GetRequiredService<IFileDialogs>().OpenDirectoryAsync(new(OwnerPolicy.PreferOwner))
                is PickerResult<IDirectoryLease>.Unavailable(PlatformUnavailableReason.OwnerUnavailable) && backend.Calls == 0,
            "A disposed window showed an unowned dialog.");

        // An application's own window services are kept.
        var own = WindowPlatformServices.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        var kept = new ServiceCollection();
        kept.AddScoped(_ => own);
        CsWebUiServices.AddRunicPlatformServices(kept, new BlockingBackend());
        await using var keptProvider = kept.BuildServiceProvider();
        await using var keptScope = keptProvider.CreateAsyncScope();
        Require(ReferenceEquals(keptScope.ServiceProvider.GetRequiredService<IWindowPlatformServices>(), own),
            "The registration replaced the application's window services.");
    }

    // A window is local while its server listens on loopback only, outside serve-only mode and without the application's
    // ShowWindow. The CS-WebUI probe (--probe-window-locality) checks a WebUI server with and without SetPublic(true).
    [Test]
    public void LocalityFollowsTheWindowServer()
    {
        var loopback = new TcpListener(IPAddress.Loopback, 0);
        var everywhere = new TcpListener(IPAddress.Any, 0);
        loopback.Start();
        everywhere.Start();
        try
        {
            int local = ((IPEndPoint)loopback.LocalEndpoint).Port, shared = ((IPEndPoint)everywhere.LocalEndpoint).Port;
            Require(WindowLocality.ListensOnLoopbackOnly(local), "A loopback listener was not local.");
            Require(!WindowLocality.ListensOnLoopbackOnly(shared), "A listener on every interface was local.");
            Require(!WindowLocality.ListensOnLoopbackOnly(0) && !WindowLocality.ListensOnLoopbackOnly(70_000), "An invalid port was local.");
            Require(WindowLocality.For(serveOnly: false, customShow: false, () => local), "A shown loopback window was not local.");
            Require(!WindowLocality.For(serveOnly: true, customShow: false, () => local), "A serve-only window was local.");
            Require(!WindowLocality.For(serveOnly: false, customShow: true, () => local), "A window ShowWindow showed was local.");
            Require(!WindowLocality.For(serveOnly: false, customShow: false, () => shared), "A public window was local.");
        }
        finally
        {
            loopback.Stop();
            everywhere.Stop();
        }
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        int closed = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        Require(!WindowLocality.ListensOnLoopbackOnly(closed), "A port nothing listens on was local.");
    }

    [Test]
    public void ArgumentsAreChecked()
    {
        Require(Throws<ArgumentNullException>(() => CsWebUiServices.AddRunicPlatformServices(new ServiceCollection(), null!))
            && Throws<ArgumentNullException>(() => CsWebUiServices.AddRunicPlatformServices(null!, new BlockingBackend())),
            "A null argument was accepted.");
    }

    // Shows one directory dialog at a time and answers when the test says so; cancellation closes it.
    private sealed class BlockingBackend : IPickerBackend
    {
        public TaskCompletionSource<OpenDirectoryOptions> Shown { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PickerResult<IDirectoryLease>> Answer { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DirectoryLease Lease { get; } = new(Path.GetTempPath());
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }
        public bool IsAvailable => true;
        public bool SupportsDirectorySelection => true;

        public void Reset()
        {
            Shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async ValueTask<PickerResult<IDirectoryLease>> OpenDirectoryAsync(OpenDirectoryOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            Shown.TrySetResult(options);
            using var registration = cancellationToken.Register(() =>
            {
                Cancelled = true;
                Answer.TrySetCanceled(cancellationToken);
            });
            return await Answer.Task;
        }

        public ValueTask<PickerResult<IReadFileLease>> OpenFileAsync(OpenFileOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Only directories are picked.");

        public ValueTask<PickerResult<ISaveFileLease>> SaveFileAsync(SaveFileOptions options, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Only directories are picked.");
    }

    private sealed class DirectoryLease(string path) : IDirectoryLease
    {
        public bool Disposed { get; private set; }
        public string DisplayName => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        public string LocalPath => path;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return true; }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
