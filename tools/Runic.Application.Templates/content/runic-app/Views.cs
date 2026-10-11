using Runic.Application.Views;
#if (viewModels == "reactiveui")
using Runic.Application.Views.ReactiveUI;
#endif

namespace RunicWindowApp;

// The build generates a typed TypeScript client for each Window and View
// declared here. AddRunicViews() registers the Views for dependency injection.
// The same Window declaration opens on Runic Desktop and CS-WebUI.
public sealed partial class WorkspaceWindow : RunicWindow<WorkspaceViewModel>;

#if (viewModels == "reactiveui")
public sealed partial class WelcomeView : ReactiveRunicView<WelcomeViewModel>;
public sealed partial class CounterView : ReactiveRunicView<CounterViewModel>;
#else
public sealed partial class WelcomeView : RunicView<WelcomeViewModel>;
public sealed partial class CounterView : RunicView<CounterViewModel>;
#endif
