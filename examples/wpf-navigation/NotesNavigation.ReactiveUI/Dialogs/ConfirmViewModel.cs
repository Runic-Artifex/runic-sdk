using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Navigation.ReactiveUI;

namespace Runic.Navigation.Examples.Notes;

// A dialog answers through its entry: the Yes command completes it with true, No dismisses it without a result.
// The caller constructs it with NavigationTarget.Create and passes it its own entry.
public sealed class ConfirmViewModel(NavigationEntryContext entry, string message) : ReactiveObject
{
    public string Message { get; } = message;
    public ReactiveCommand<RxVoid, NavigationOutcome> YesCommand { get; } = entry.CreateCompleteCommand(_ => Task.FromResult(true));
    public ReactiveCommand<RxVoid, NavigationOutcome> NoCommand { get; } = entry.CreateDismissCommand();
}
