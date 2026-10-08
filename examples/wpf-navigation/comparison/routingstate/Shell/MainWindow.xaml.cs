using Comparison.RoutingStateApp.S2;

namespace Comparison.RoutingStateApp.Shell;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Dialogs.Confirm.RegisterHandler(context => // [S2]
            context.SetOutput(new ConfirmWindow(context.Input) { Owner = this }.ShowDialog() == true)); // [S2]
    }
}
