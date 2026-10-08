using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Runic.Navigation.Examples.Notes;

public sealed class NotesListViewModel : ObservableObject, INavigationResume
{
    private readonly NoteStore _store;

    public NotesListViewModel(NoteStore store, AppRegions regions)
    {
        _store = store;
        Notes = new(store.All);
        // The ViewModel type and its typed input are the parameter contract; no string keys.
        OpenCommand = new AsyncRelayCommand<Note>(note => regions.Main.PushAsync<NoteDetailViewModel, int>(note!.Id).AsTask(),
            note => note is not null);
        OpenSettingsCommand = new AsyncRelayCommand(() => regions.Main.PushAsync<SettingsViewModel>().AsTask()); // [S3]
        DeleteCommand = new AsyncRelayCommand<Note>(async note => // [S2]
        { // [S2]
            var answer = await regions.Dialog.PushForResult<bool>( // [S2]
                NavigationTarget.Create<ConfirmViewModel, string>($"Delete '{note!.Title}'?")).Completion; // [S2]
            if (answer is NavigationCompletion<bool>.Completed { Value: true }) // [S2]
            { // [S2]
                store.Delete(note.Id); // [S2]
                Notes.Remove(note); // [S2]
            } // [S2]
        }, note => note is not null); // [S2]
    }

    public ObservableCollection<Note> Notes { get; }
    public IAsyncRelayCommand<Note> OpenCommand { get; }
    public IAsyncRelayCommand OpenSettingsCommand { get; } // [S3]
    public IAsyncRelayCommand<Note> DeleteCommand { get; } // [S2]

    // Runs each time the list becomes current again, for example after Back from a note.
    public ValueTask ResumeAsync(NavigationResume resume, CancellationToken cancellationToken)
    {
        Notes.ReplaceWith(_store.All);
        return ValueTask.CompletedTask;
    }
}
