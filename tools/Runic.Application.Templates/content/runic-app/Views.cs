using Runic.Application.Views;
#if (host == "desktop")
using Runic.Application.Views.Desktop;
#else
using Runic.Application.Views.CsWebUi;
#endif
#if (viewModels == "reactiveui")
using Runic.Application.Views.ReactiveUI;
#endif
#if (host == "desktop")
using Runic.Desktop;
#endif

namespace RunicWindowApp;

// The build generates a typed TypeScript client for each Window and View
// declared here. AddRunicViews() registers the Views for dependency injection.
#if (host == "desktop" && viewModels == "reactiveui")
public sealed partial class WorkspaceWindow(DesktopBridgeWindow<WorkspaceViewModel> host)
    : ReactiveRunicWindow<WorkspaceViewModel>(host.ViewModel), IAsyncDisposable
{
    public DesktopWindow Presentation => host.Presentation;
    public ValueTask DisposeAsync() => host.DisposeAsync();
}

public sealed partial class WelcomeView : ReactiveRunicView<WelcomeViewModel>;
public sealed partial class CounterView : ReactiveRunicView<CounterViewModel>;
#elif (host == "desktop")
public sealed partial class WorkspaceWindow(DesktopBridgeWindow<WorkspaceViewModel> host)
    : RunicWindow<WorkspaceViewModel>(host.ViewModel), IAsyncDisposable
{
    public DesktopWindow Presentation => host.Presentation;
    public ValueTask DisposeAsync() => host.DisposeAsync();
}

public sealed partial class WelcomeView : RunicView<WelcomeViewModel>;
public sealed partial class CounterView : RunicView<CounterViewModel>;
#elif (viewModels == "reactiveui")
public sealed partial class WorkspaceWindow(CsWebUiBridgeWindow<WorkspaceViewModel> host)
    : ReactiveRunicWindow<WorkspaceViewModel>(host.ViewModel), IDisposable
{
    public void SetRootFolder(string path) => host.SetRootFolder(path);
    public void Show(string content) => host.Show(content);
    public void Dispose() => host.Dispose();
}

public sealed partial class WelcomeView : ReactiveRunicView<WelcomeViewModel>;
public sealed partial class CounterView : ReactiveRunicView<CounterViewModel>;
#else
public sealed partial class WorkspaceWindow(CsWebUiBridgeWindow<WorkspaceViewModel> host)
    : CsWebUiWindow<WorkspaceViewModel>(host);

public sealed partial class WelcomeView : RunicView<WelcomeViewModel>;
public sealed partial class CounterView : RunicView<CounterViewModel>;
#endif
