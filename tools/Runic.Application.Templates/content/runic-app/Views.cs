using Runic.Application.Views;
#if (desktopHost)
using Runic.Application.Views.Desktop;
#else
using Runic.Application.Views.CsWebUi;
#endif
#if (viewModels == "reactiveui")
using Runic.Application.Views.ReactiveUI;
#endif
#if (desktopHost)
using Runic.Desktop;
#endif

namespace RunicWindowApp;

// The build generates a typed TypeScript client for each Window and View
// declared here. AddRunicViews() registers the Views for dependency injection.
#if (desktopHost && viewModels == "reactiveui")
public sealed partial class WorkspaceWindow(DesktopBridgeWindow<WorkspaceViewModel> host)
    : ReactiveRunicWindow<WorkspaceViewModel>(host.ViewModel), IAsyncDisposable
{
    public DesktopWindow Presentation => host.Presentation;
#if (gtk4)
    // Pass to the Runic.Platform provider for this backend for native file dialogs and the clipboard, for
    // example WindowsPlatformProvider.CreateFileDialogs(NativeOwner) on Windows or, on Linux with GTK 4,
    // PortalPlatformProvider.CreateFileDialogs(Gtk4PlatformProvider.CreatePortalWindowOwner(NativeOwner)).
    // Do not use LinuxPlatformProvider here: it parents dialogs through GTK 3.
#else
    // Pass to the Runic.Platform provider for this backend for native file dialogs and the clipboard, for
    // example WindowsPlatformProvider.CreateFileDialogs(NativeOwner) on Windows or, on Linux with GTK 3,
    // LinuxPlatformProvider.CreateFileDialogs(NativeOwner).
#endif
    public DesktopNativeOwner NativeOwner => host.NativeOwner;
    public ValueTask DisposeAsync() => host.DisposeAsync();
}

public sealed partial class WelcomeView : ReactiveRunicView<WelcomeViewModel>;
public sealed partial class CounterView : ReactiveRunicView<CounterViewModel>;
#elif (desktopHost)
public sealed partial class WorkspaceWindow(DesktopBridgeWindow<WorkspaceViewModel> host)
    : RunicWindow<WorkspaceViewModel>(host.ViewModel), IAsyncDisposable
{
    public DesktopWindow Presentation => host.Presentation;
#if (gtk4)
    // Pass to the Runic.Platform provider for this backend for native file dialogs and the clipboard, for
    // example WindowsPlatformProvider.CreateFileDialogs(NativeOwner) on Windows or, on Linux with GTK 4,
    // PortalPlatformProvider.CreateFileDialogs(Gtk4PlatformProvider.CreatePortalWindowOwner(NativeOwner)).
    // Do not use LinuxPlatformProvider here: it parents dialogs through GTK 3.
#else
    // Pass to the Runic.Platform provider for this backend for native file dialogs and the clipboard, for
    // example WindowsPlatformProvider.CreateFileDialogs(NativeOwner) on Windows or, on Linux with GTK 3,
    // LinuxPlatformProvider.CreateFileDialogs(NativeOwner).
#endif
    public DesktopNativeOwner NativeOwner => host.NativeOwner;
    public ValueTask DisposeAsync() => host.DisposeAsync();
}

public sealed partial class WelcomeView : RunicView<WelcomeViewModel>;
public sealed partial class CounterView : RunicView<CounterViewModel>;
#elif (viewModels == "reactiveui")
public sealed partial class WorkspaceWindow(CsWebUiBridgeWindow<WorkspaceViewModel> host)
    : ReactiveRunicWindow<WorkspaceViewModel>(host.ViewModel), IAsyncDisposable
{
    public void SetRootFolder(string path) => host.SetRootFolder(path);
    public void Show(string content) => host.Show(content);
    public ValueTask DisposeAsync() => host.DisposeAsync();
}

public sealed partial class WelcomeView : ReactiveRunicView<WelcomeViewModel>;
public sealed partial class CounterView : ReactiveRunicView<CounterViewModel>;
#else
public sealed partial class WorkspaceWindow(CsWebUiBridgeWindow<WorkspaceViewModel> host)
    : CsWebUiWindow<WorkspaceViewModel>(host);

public sealed partial class WelcomeView : RunicView<WelcomeViewModel>;
public sealed partial class CounterView : RunicView<CounterViewModel>;
#endif
