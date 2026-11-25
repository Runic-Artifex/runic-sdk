using Runic.Application.Views;

namespace SharedLibraryApp;

// Only the application's own ViewModel gets a View here. AboutView and SupportView come from SharedUi.
public sealed partial class ShellWindow : RunicWindow<ShellViewModel>;
