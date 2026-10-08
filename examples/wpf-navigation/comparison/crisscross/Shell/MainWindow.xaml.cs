using Comparison.CrissCrossApp.S1;
using Comparison.CrissCrossApp.S2;
using CrissCross;
using ReactiveUI;
using ReactiveUI.Primitives.Disposables;

namespace Comparison.CrissCrossApp.Shell;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Dialogs.Confirm.RegisterHandler(context => // [S2]
            context.SetOutput(new ConfirmWindow(context.Input) { Owner = this }.ShowDialog() == true)); // [S2]
        this.WhenActivated((MultipleDisposable disposables) =>
        {
            NavBack.Command = ReactiveCommand.Create(() => this.NavigateBack(), this.CanNavigateBack()); // [S4]
            this.NavigateToView(new NavigationKeyRequest<NotesListViewModel>()); // [S1]
        });
    }
}
