using Runic.Application.Views;

namespace SharedUi;

// The library declares the Views of its ViewModels, so it owns their generated TypeScript. Applications that map
// this assembly (RunicApplicationFrontendClientModule) declare none of their own.
public sealed partial class AboutView : RunicView<AboutViewModel>;
public sealed partial class SupportView : RunicView<SupportViewModel>;
