using Comparison.RoutingStateApp.S1;
using ReactiveUI;
using ReactiveUI.Primitives;

namespace Comparison.RoutingStateApp.Shell;

public sealed class ShellViewModel : ReactiveObject, IScreen
{
    public ShellViewModel(NoteStore store)
    {
        // RoutingState has no guards: Back goes through a command that asks the current page first.
        GoBack = ReactiveCommand.CreateFromTask(async () => // [S4]
        { // [S4]
            if (Router.GetCurrentViewModel() is ILeaveGuard guard && !await guard.CanLeaveAsync()) // [S1]
            { // [S1]
                return; // [S1]
            } // [S1]

            await Router.NavigateBack.Execute().FirstAsync(); // [S4]
        }, Router.CanNavigateBack); // [S4]
        Router.Navigate.Execute(new NotesListViewModel(this, store)).Subscribe(); // [S1]
    }

    public RoutingState Router { get; } = new();
    public ReactiveCommand<RxVoid, RxVoid> GoBack { get; } // [S4]
}
