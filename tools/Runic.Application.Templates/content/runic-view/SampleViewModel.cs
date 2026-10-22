#if (viewModels == "reactiveui")
using ReactiveUI;
using ReactiveUI.Primitives;

namespace RunicViewNamespace;

// Register it with the application's other ViewModels, for example services.AddScoped<SampleViewModel>().
public sealed class SampleViewModel : ReactiveObject
{
    private string _name = "";

    public SampleViewModel() =>
        ClearCommand = ReactiveCommand.Create(() => { Name = ""; });

    // The public setter lets the frontend set Name: the generated client has setName. A private setter would make
    // it read-only state.
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ClearCommand { get; }
}
#else
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RunicViewNamespace;

// Register it with the application's other ViewModels, for example services.AddScoped<SampleViewModel>().
public sealed partial class SampleViewModel : ObservableObject
{
    public SampleViewModel() => Name = "";

    // The public setter lets the frontend set Name: the generated client has setName. A private setter would make
    // it read-only state.
    [ObservableProperty]
    public partial string Name { get; set; }

    [RelayCommand]
    private void Clear() => Name = "";
}
#endif
