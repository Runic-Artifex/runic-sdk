using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Navigation.ReactiveUI;

namespace Runic.Navigation.Examples.Notes;

public sealed class NotesListViewModel : ReactiveObject
{
    private Note? _selected;

    public NotesListViewModel(NoteStore store, AppRegions regions, NavigationEntryContext entry)
    {
        Notes = new(store.All);
        // The commands act on the bound selection; ReactiveCommand.CanExecute is an observable of it.
        var hasSelection = this.WhenAnyValue(vm => vm.Selected, (Note? note) => note is not null);
        // The ViewModel type and its typed input are the parameter contract; no string keys.
        OpenCommand = ReactiveCommand.CreateFromTask(async token =>
            { await regions.Main.PushAsync<NoteDetailViewModel, int>(Selected!.Id, cancellationToken: token); }, hasSelection);
        OpenSettingsCommand = ReactiveCommand.CreateFromTask(async token => { await regions.Main.PushAsync<SettingsViewModel>(cancellationToken: token); }); // [S3]
        DeleteCommand = ReactiveCommand.CreateFromTask(async token => // [S2]
        { // [S2]
            var note = Selected!; // [S2]
            var answer = await regions.Dialog.PushForResult<bool>(NavigationTarget.Create<ConfirmViewModel>( // [S2]
                (_, dialog) => new ConfirmViewModel(dialog, $"Delete '{note.Title}'?")), cancellationToken: token).Completion; // [S2]
            if (answer is NavigationCompletion<bool>.Completed { Value: true }) // [S2]
            { // [S2]
                store.Delete(note.Id); // [S2]
                Notes.Remove(note); // [S2]
            } // [S2]
        }, hasSelection); // [S2]
        // Each time the list becomes current again, for example after Back from a note. Ends when the entry retires.
        entry.WhenNavigatedTo((_, _) => Notes.ReplaceWith(store.All));
    }

    public ObservableCollection<Note> Notes { get; }
    public Note? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }
    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenSettingsCommand { get; } // [S3]
    public ReactiveCommand<RxVoid, RxVoid> DeleteCommand { get; } // [S2]
}
