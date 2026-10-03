using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace RunicWindowApp;

// The build generates a typed TypeScript client for each Window and View
// declared here. AddRunicViews() registers the Views for dependency injection.
public sealed partial class WorkspaceWindow(CsWebUiBridgeWindow<WorkspaceViewModel> host)
    : CsWebUiWindow<WorkspaceViewModel>(host);

public sealed partial class WelcomeView : RunicView<WelcomeViewModel>;
public sealed partial class CounterView : RunicView<CounterViewModel>;
