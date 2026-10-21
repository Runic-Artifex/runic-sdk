using System.ComponentModel;
using Runic.Application.Views;

namespace DevLiveAssets;

public sealed partial class StatusWindow : RunicWindow<StatusViewModel>;

public sealed class StatusViewModel : INotifyPropertyChanged
{
    public string Title => "dev-live-assets";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}
