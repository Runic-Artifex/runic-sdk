using System.ComponentModel;
using Runic.Application.Views;

// A ViewModel and View in the global namespace previously generated
// "namespace ;" and bridge references such as global::.NameBridge.
#pragma warning disable CA1050 // The global namespace is the shape under test.
public sealed class GlobalNamespaceViewModel : INotifyPropertyChanged
{
    public string Title { get; } = "global";
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class GlobalNamespaceWindow(GlobalNamespaceViewModel model) : RunicWindow<GlobalNamespaceViewModel>(model);
#pragma warning restore CA1050
