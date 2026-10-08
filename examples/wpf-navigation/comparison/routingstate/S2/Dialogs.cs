using ReactiveUI;

namespace Comparison.RoutingStateApp.S2;

// ReactiveUI's idiom for a ViewModel asking the View a question: an Interaction, handled by the shell.
public static class Dialogs
{
    public static Interaction<string, bool> Confirm { get; } = new();
}
