using System.Collections.ObjectModel;
using Comparison.PrismApp.S2;
using Comparison.PrismApp.S3;
using Prism.Commands;
using Prism.Dialogs;
using Prism.Mvvm;
using Prism.Navigation;
using Prism.Navigation.Regions;

namespace Comparison.PrismApp.S1;

public sealed class NotesListViewModel : BindableBase, IRegionAware
{
    private readonly NoteStore _store;

    public NotesListViewModel(NoteStore store, IRegionManager regions, IDialogService dialogs)
    {
        _store = store;
        OpenCommand = new DelegateCommand<Note>(note =>
            regions.RequestNavigate("Main", nameof(NoteDetailView), new NavigationParameters { { "id", note.Id } }));
        OpenSettingsCommand = new DelegateCommand(() => regions.RequestNavigate("Main", nameof(SettingsView))); // [S3]
        DeleteCommand = new AsyncDelegateCommand<Note>(async note => // [S2]
        { // [S2]
            var result = await dialogs.ShowDialogAsync(nameof(ConfirmDialog), // [S2]
                new DialogParameters { { "message", $"Delete '{note.Title}'?" } }); // [S2]
            if (result.Result == ButtonResult.OK) // [S2]
            { // [S2]
                store.Delete(note.Id); // [S2]
                Notes.Remove(note); // [S2]
            } // [S2]
        }); // [S2]
    }

    public ObservableCollection<Note> Notes { get; } = [];
    public DelegateCommand<Note> OpenCommand { get; }
    public DelegateCommand OpenSettingsCommand { get; } // [S3]
    public AsyncDelegateCommand<Note> DeleteCommand { get; } // [S2]

    // Resume on return: the journal re-navigates to this kept-alive view.
    public void OnNavigatedTo(NavigationContext context) => Notes.ReplaceWith(_store.All);
    public bool IsNavigationTarget(NavigationContext context) => true;
    public void OnNavigatedFrom(NavigationContext context) { }
}
