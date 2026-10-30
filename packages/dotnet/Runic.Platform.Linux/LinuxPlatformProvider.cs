using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using Runic.Platform.Linux.Portal;

namespace Runic.Platform.Linux;

// The window services of a GTK 3 Desktop window. Internal: Runic.Application.Views.Desktop selects them through
// DesktopPlatformServices; the application-scoped services are PortalApplication's.
[SupportedOSPlatform("linux")]
internal static class LinuxPlatformProvider
{
    // Owned file and URI handoffs for a GTK 3 window.
    internal static IDesktopFileLauncher CreateFileLauncher(INativeWindowOwner owner)
    { ArgumentNullException.ThrowIfNull(owner); return PortalApplication.Inferred.CreateFileLauncher(new Gtk3PortalWindowOwner(owner)); }

    // Portal dialogs parented to the GTK 3 window.
    internal static IPickerBackend CreateFileDialogs(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return PortalApplication.Inferred.CreateFileDialogs(new Gtk3PortalWindowOwner(owner));
    }

    // GTK-native dialogs for an unsandboxed compatibility application. Inside Flatpak or Snap they are unavailable, as
    // every Runic.Platform service reports an environment it cannot serve: use the portal dialogs.
    internal static IPickerBackend CreateGtkNativeFileDialogs(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new NativePickerBackend(owner, IsSandboxed() ? new SandboxedPicker() : new LinuxFilePicker(owner));
    }

    internal static bool IsSandboxed() => File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP") is not null;

    // GtkMessageDialogs transient for the GTK 3 window.
    internal static IDesktopMessageBoxes CreateMessageBoxes(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new Gtk3MessageBoxes(owner);
    }

    // The application's GtkMessageDialogs without a parent window, on the GTK 3 thread (GLibApplicationOwner).
    internal static IDesktopMessageBoxes CreateUnownedMessageBoxes() => new Gtk3MessageBoxes(GLibApplicationOwner.Shared, unowned: true);

    // The thread that runs GLib's default main context, for the application's GTK work without a window.
    internal static INativeWindowOwner ApplicationOwner => GLibApplicationOwner.Shared;

    // Whether a thread runs GLib's default main context, so the application's GTK work can run.
    internal static bool IsApplicationLoopRunning => GLibApplicationOwner.IsRunning;

    // The GTK clipboard; it is IAsyncDisposable and must be disposed before the owner's dispatcher stops.
    internal static LinuxTextClipboard CreateTextClipboard(INativeWindowOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new LinuxTextClipboard(owner);
    }

    // Reports every dialog unavailable (BackendUnavailable) through NativePickerBackend.
    private sealed class SandboxedPicker : INativeFilePicker, INativeDirectoryPicker
    {
        public ValueTask<IReadOnlyList<NativeFileSelection>?> SelectAsync(NativeFileDialogRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<IReadOnlyList<NativeFileSelection>?>(new NativeBackendUnavailableException("Sandboxed applications must use portal file dialogs."));
        public ValueTask<NativeDirectorySelection?> SelectDirectoryAsync(string? initialDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromException<NativeDirectorySelection?>(new NativeBackendUnavailableException("Sandboxed applications must use portal file dialogs."));
    }
}
