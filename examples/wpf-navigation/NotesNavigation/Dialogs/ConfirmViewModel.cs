using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Navigation.Examples.Notes;

// A dialog answers through its entry: CompleteAsync(true) is the result, DismissAsync ends it without one.
public sealed class ConfirmViewModel : ObservableObject, INavigationInitialize<string>
{
    // The navigator constructs the dialog and passes it its own entry.
    public ConfirmViewModel(NavigationEntryContext entry)
    {
        YesCommand = new AsyncRelayCommand(() => entry.CompleteAsync(true).AsTask());
        NoCommand = new AsyncRelayCommand(() => entry.DismissAsync().AsTask());
    }

    public string Message { get; private set; } = "";
    public IAsyncRelayCommand YesCommand { get; }
    public IAsyncRelayCommand NoCommand { get; }

    public ValueTask InitializeAsync(NavigationEntryContext entry, string message, CancellationToken cancellationToken)
    {
        Message = message;
        return ValueTask.CompletedTask;
    }
}
