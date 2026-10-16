using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Application.Testing.Tests;

// A property named in [RelayCommand(CanExecute = ...)] whose wire name is the
// command's can<Name> availability (#305). CanArchive is the same for a command
// with an argument, whose availability is a query, so it stays state.
public sealed partial class ToolkitAvailabilityViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    public partial string Target { get; set; } = "";

    /// <summary>Whether a folder is chosen.</summary>
    public bool CanCreate => Target.Length > 0;

    public bool CanArchive => Target.Length > 1;

    public int Created { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void Create() => Created++;

    [RelayCommand(CanExecute = nameof(CanArchive))]
    private void Archive(string name) => Target = name;
}

public sealed partial class ToolkitAvailabilityWindow
    : Runic.Application.Views.RunicWindow<ToolkitAvailabilityViewModel>;
