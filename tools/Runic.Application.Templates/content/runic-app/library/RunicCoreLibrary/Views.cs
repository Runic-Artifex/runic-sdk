using Runic.Application.Views;
#if (viewModels == "reactiveui")
using Runic.Application.Views.ReactiveUI;
#endif

namespace RunicCoreNamespace;

// The library declares the View of its ViewModel, so its build owns the TypeScript client: it writes it into
// Frontend/src/generated, the ./generated export of the library's frontend package. An application that presents an
// AboutViewModel registers this View with its own AddRunicViews() and imports the client from the package.
#if (viewModels == "reactiveui")
public sealed partial class AboutView : ReactiveRunicView<AboutViewModel>;
#else
public sealed partial class AboutView : RunicView<AboutViewModel>;
#endif
