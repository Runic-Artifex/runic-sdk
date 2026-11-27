#if (viewModels == "reactiveui")
using ReactiveUI;
using ReactiveUI.Primitives;

namespace RunicCoreNamespace;

// A ViewModel that any application of the solution can present, with its View in Views.cs.
public sealed class AboutViewModel : ReactiveObject
{
    private bool _expanded;

    public AboutViewModel() =>
        ToggleDetailsCommand = ReactiveCommand.Create(() => { Expanded = !Expanded; });

    public string Library => "RunicCoreLibrary";

    public string Version { get; } = typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    // Whether the panel shows its details. The page reads it; ToggleDetailsCommand changes it.
    public bool Expanded
    {
        get => _expanded;
        private set => this.RaiseAndSetIfChanged(ref _expanded, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ToggleDetailsCommand { get; }
}
#else
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RunicCoreNamespace;

// A ViewModel that any application of the solution can present, with its View in Views.cs.
public sealed partial class AboutViewModel : ObservableObject
{
    public string Library => "RunicCoreLibrary";

    public string Version { get; } = typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    // Whether the panel shows its details. The page reads it; ToggleDetailsCommand changes it.
    [ObservableProperty]
    public partial bool Expanded { get; private set; }

    [RelayCommand]
    private void ToggleDetails() => Expanded = !Expanded;
}
#endif
