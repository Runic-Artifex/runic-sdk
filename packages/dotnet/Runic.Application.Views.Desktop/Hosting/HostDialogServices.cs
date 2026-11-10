using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.Desktop.Hosting;

// The owned dialogs of one hosted window (DesktopPlatformServices.CreateHostDialogs): the provider's picker, which
// the host shows itself to hand the guest paths, and the window's message boxes. Unavailable names why there are none;
// owner, when given, holds what the services keep for the window and is released with them.
internal sealed class HostDialogServices(IDesktopMessageBoxes messageBoxes, INativeFilePicker? picker, PlatformUnavailableReason? unavailable,
    IAsyncDisposable? owner = null) : IAsyncDisposable
{
    internal IDesktopMessageBoxes MessageBoxes => messageBoxes;

    internal INativeFilePicker? Picker => picker;

    internal PlatformUnavailableReason? Unavailable => unavailable;

    public async ValueTask DisposeAsync()
    {
        if (owner is null) return;
        try { await owner.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
}

// The menu commands of a hosted window (DesktopWindow.MenuCommands): a guest's item that the window's bar shows runs
// the guest's command through menu-invoke; any other item, such as one a policy added, goes to the host
// application's commands. A choice or shortcut of a bar the window showed before the guest's latest one carries an id
// that bar alone had, and is dropped (host-protocol.md, The guest's menu bar).
internal sealed class HostedMenuTarget(RunicHostedWindow window) : IDesktopMenuCommandTarget
{
    public bool CanExecute(DesktopMenuItem item) =>
        HostedWire.ItemId(item) is { } id && window.IsOpen && window.Application.IsChannelOpen && window.ShowsMenuItem(id);

    public void Execute(DesktopMenuItem item)
    {
        if (HostedWire.ItemId(item) is { } id && window.ShowsMenuItem(id)) window.Application.InvokeMenuItem(window, id);
    }
}
