using System.Windows;
using System.Windows.Controls;

namespace WpfSingle;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}

public sealed class EditorHost : ContentControl;
