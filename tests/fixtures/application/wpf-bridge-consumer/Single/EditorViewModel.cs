using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Runic.Application.Views;

namespace WpfSingle;

public sealed class EditorViewModel : INotifyPropertyChanged
{
    private string _title = "";

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    // A WPF-typed member that the Bridge does not expose (#269).
    [RunicIgnore]
    public ImageSource? Preview { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    // An iterator over WPF objects: its state machine is a nested type that
    // implements IEnumerable<UIElement> (#269).
    internal static IEnumerable<UIElement> Children(Panel panel)
    {
        foreach (UIElement child in panel.Children) yield return child;
    }
}

public sealed partial class EditorWebView : RunicView<EditorViewModel>;
