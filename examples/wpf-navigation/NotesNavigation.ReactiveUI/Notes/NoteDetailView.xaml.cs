using System.Windows;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;

namespace Runic.Navigation.Examples.Notes;

public partial class NoteDetailView
{
    public NoteDetailView()
    {
        InitializeComponent();
        // Answers the ViewModel's leave confirmation while this View is shown.
        this.WhenActivated((MultipleDisposable disposables) => ViewModel!.ConfirmDiscard.RegisterHandler(context => context.SetOutput(
            MessageBox.Show(context.Input, "Notes", MessageBoxButton.OKCancel) == MessageBoxResult.OK)).DisposeWith(disposables));
    }
}

// XAML in a Runic.Navigation namespace can't name ReactiveUI's generic ReactiveUserControl, so the View derives from this.
public class NoteDetailViewBase : ReactiveUserControl<NoteDetailViewModel>;
