using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Navigation.Examples.Notes;

// A dialog answers through its entry: CompleteAsync(true) is the result, DismissAsync ends it without one.
public sealed class ConfirmViewModel : ObservableObject, INavigationInitialize<string>
{
    private NavigationEntryContext? _entry;

    public ConfirmViewModel()
    {
        YesCommand = new AsyncRelayCommand(() => _entry!.CompleteAsync(true).AsTask());
        NoCommand = new AsyncRelayCommand(() => _entry!.DismissAsync().AsTask());
    }

    public string Message { get; private set; } = "";
    public IAsyncRelayCommand YesCommand { get; }
    public IAsyncRelayCommand NoCommand { get; }

    public ValueTask InitializeAsync(NavigationEntryContext entry, string message, CancellationToken cancellationToken)
    {
        _entry = entry;
        Message = message;
        return ValueTask.CompletedTask;
    }
}
