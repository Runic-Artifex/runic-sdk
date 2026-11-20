using System.Windows;
using System.Windows.Controls;

namespace WpfReactive;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}

public sealed class NotesHost : ContentControl;
