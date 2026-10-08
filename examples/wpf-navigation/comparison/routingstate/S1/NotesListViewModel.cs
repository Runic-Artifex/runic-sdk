using System.Collections.ObjectModel;
using Comparison.RoutingStateApp.S2;
using Comparison.RoutingStateApp.S3;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;

namespace Comparison.RoutingStateApp.S1;

public sealed class NotesListViewModel : ReactiveObject, IRoutableViewModel, IActivatableViewModel
{
    public NotesListViewModel(IScreen host, NoteStore store)
    {
        // The commands act on the bound selection; ReactiveCommand.CanExecute is an observable of it.
        var hasSelection = this.WhenAnyValue(x => x.Selected, (Note? note) => note is not null);
        HostScreen = host;
        Open = ReactiveCommand.CreateFromObservable(() =>
            host.Router.Navigate.Execute(new NoteDetailViewModel(host, store, Selected!.Id)), hasSelection);
        OpenSettings = ReactiveCommand.CreateFromObservable(() => host.Router.Navigate.Execute(new SettingsViewModel(host))); // [S3]
        Delete = ReactiveCommand.CreateFromTask(async () => // [S2]
        { // [S2]
            var note = Selected!; // [S2]
            if (await Dialogs.Confirm.Handle($"Delete '{note.Title}'?")) // [S2]
            { // [S2]
                store.Delete(note.Id); // [S2]
                Notes.Remove(note); // [S2]
            } // [S2]
        }, hasSelection); // [S2]

        // Resume on return: the view is recreated when this page becomes current again.
        this.WhenActivated((MultipleDisposable _) => Notes.ReplaceWith(store.All));
    }

    public string UrlPathSegment => "notes";
    public IScreen HostScreen { get; }
    public ViewModelActivator Activator { get; } = new();
    private Note? _selected;
    public Note? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }
    public ObservableCollection<Note> Notes { get; } = [];
    public ReactiveCommand<RxVoid, IRoutableViewModel> Open { get; }
    public ReactiveCommand<RxVoid, IRoutableViewModel> OpenSettings { get; } // [S3]
    public ReactiveCommand<RxVoid, RxVoid> Delete { get; } // [S2]
}
