using System.Collections.ObjectModel;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using Comparison.CrissCrossApp.S2;
using Comparison.CrissCrossApp.S3;
using CrissCross;
using ReactiveUI;

namespace Comparison.CrissCrossApp.S1;

public sealed class NotesListViewModel : RxObject
{
    private readonly NoteStore _store;

    public NotesListViewModel(NoteStore store)
    {
        // ReactiveCommand.CanExecute is an observable, not a function of the parameter: track the selection.
        var hasSelection = this.WhenAnyValue(x => x.Selected, (Note? note) => note is not null);
        _store = store;
        Open = ReactiveCommand.Create<Note>(note => this.NavigateToView(
            new NavigationKeyRequest<NoteDetailViewModel> { Options = new() { Parameter = note.Id } }), hasSelection);
        OpenSettings = ReactiveCommand.Create(() => this.NavigateToView(new NavigationKeyRequest<SettingsViewModel>())); // [S3]
        Delete = ReactiveCommand.CreateFromTask<Note>(async note => // [S2]
        { // [S2]
            if (await Dialogs.Confirm.Handle($"Delete '{note.Title}'?")) // [S2]
            { // [S2]
                store.Delete(note.Id); // [S2]
                Notes.Remove(note); // [S2]
            } // [S2]
        }, hasSelection); // [S2]
    }

    private Note? _selected;
    public Note? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }
    public ObservableCollection<Note> Notes { get; } = [];
    public ReactiveCommand<Note, RxVoid> Open { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenSettings { get; } // [S3]
    public ReactiveCommand<Note, RxVoid> Delete { get; } // [S2]

    // Resume on return: the singleton is navigated to again.
    public override void WhenNavigatedTo(IViewModelNavigationEventArgs e, MultipleDisposable disposables) =>
        Notes.ReplaceWith(_store.All);
}
