using System.Windows;
using ReactiveUI;

namespace WpfReactive;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}

public sealed class EditorPanel : ReactiveUserControl<EditorViewModel>;
