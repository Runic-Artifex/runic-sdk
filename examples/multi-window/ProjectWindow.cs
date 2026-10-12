using Runic.Application.Views;

namespace MultiWindow;

// Every open Window is a ProjectWindow with its own scope and ProjectViewModel.
public sealed partial class ProjectWindow : RunicWindow<ProjectViewModel>;
