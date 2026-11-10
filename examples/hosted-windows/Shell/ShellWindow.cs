using Runic.Application.Views;

namespace HostedShell;

// The shell's own Window. The counter's windows are native windows of the shell too, but they show the counter's page.
public sealed partial class ShellWindow : RunicWindow<ShellViewModel>;
