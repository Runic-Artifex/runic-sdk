using System.Windows;
using System.Windows.Controls;

namespace SplitApp;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}

public sealed class EditorHost : ContentControl;
