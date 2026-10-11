using System.ComponentModel;
using Runic.Application.Views;

namespace SplitModel;

public sealed class EditorViewModel : INotifyPropertyChanged
{
    private string _title = "";

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class EditorWebView : RunicView<EditorViewModel>;
