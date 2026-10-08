using System.Windows;

namespace Comparison.CrissCrossApp.S2;

public partial class ConfirmWindow
{
    public ConfirmWindow(string message)
    {
        InitializeComponent();
        DataContext = message;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
