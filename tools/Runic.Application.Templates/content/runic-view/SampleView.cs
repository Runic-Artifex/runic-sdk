using Runic.Application.Views;
#if (viewModels == "reactiveui" && !window)
using Runic.Application.Views.ReactiveUI;
#endif

namespace RunicViewNamespace;

// The build generates a typed TypeScript client for this declaration, in the frontend's src/generated folder.
#if (window)
// A host opens it with OpenWindowAsync<SampleWindow>(); the generated AddRunicViews() registers its Bridge.
public sealed partial class SampleWindow : RunicWindow<SampleViewModel>;
#elif (viewModels == "reactiveui")
// A Window or another View shows it from a ViewModel property; AddRunicViews() registers it.
public sealed partial class SampleView : ReactiveRunicView<SampleViewModel>;
#else
// A Window or another View shows it from a ViewModel property; AddRunicViews() registers it.
public sealed partial class SampleView : RunicView<SampleViewModel>;
#endif
