using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// presentation-epoch/1 (specs/application/README.md, "Presentation epochs"): commands scoped to their presentation, on a
// CommunityToolkit [RelayCommand] method and on a command property, beside an unscoped command.
public sealed partial class ScopedWizardViewModel : ObservableObject
{
    public ScopedWizardViewModel() => BackCommand = new RelayCommand(() =>
    {
        Page--;
        RunicPresentation.Renew(this);
    }, () => Page > 1);

    [ObservableProperty]
    public partial int Page { get; set; } = 1;

    [RelayCommand]
    [RunicPresentationScoped]
    private void Next()
    {
        Page++;
        RunicPresentation.Renew(this);
    }

    [RunicPresentationScoped]
    public IRelayCommand BackCommand { get; }

    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "A [RelayCommand] source method.")]
    private void Ping() { }
}

public sealed partial class ScopedWizardWindow : RunicWindow<ScopedWizardViewModel>;
