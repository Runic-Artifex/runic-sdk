using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.App;

// The page reads Count and runs Increment through the generated TypeScript client. The client, and the page built
// against it, describe this class exactly: the View's fingerprint in the web bundle is the hash of that contract, so a
// change here after the bundle was written stops the publish with RUNICBRIDGE094.
public sealed partial class CounterViewModel : ObservableObject
{
    [ObservableProperty]
    public partial int Count { get; private set; }

    [RelayCommand]
    private void Increment() => Count++;
}
