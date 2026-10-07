using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;

namespace NotesWindowViews;

// These interfaces describe independently presentable regions. They are not a
// second visual tree: the frontend decides where and whether to mount them.
public interface IMainViewModel { }
public interface IDocumentPaneViewModel { }
public interface IDialogViewModel { }

/// <summary>A pane of the document page.</summary>
public enum DocumentPane
{
    /// <summary>The note editor.</summary>
    Editor,

    /// <summary>The rendered note.</summary>
    Preview,
}

public sealed class WorkspaceNavigation(HomeViewModel home, DocumentViewModel document)
    : ObservableObject
{
    private IMainViewModel _main = home;
    private IDialogViewModel? _dialog;

    public IMainViewModel Main
    {
        get => _main;
        private set => SetProperty(ref _main, value);
    }

    public IDialogViewModel? Dialog
    {
        get => _dialog;
        private set => SetProperty(ref _dialog, value);
    }

    public void OpenDocument() => Main = document;

    public void OpenHome()
    {
        if (ReferenceEquals(Main, document) && document.Editor.IsDirty)
        {
            // This is an in-page modal. The coordinator owns the transient
            // ViewModel; closing it clears the Shell's dialog outlet.
            Dialog ??= new ConfirmNavigationViewModel(
                "Discard the unsaved edits and return Home?",
                () => { document.Editor.DiscardChanges(); Dialog = null; Main = home; },
                () => Dialog = null);
            return;
        }
        Main = home;
    }
}

public partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceNavigation _navigation;

    public ShellViewModel(SidebarViewModel sidebar, WorkspaceNavigation navigation)
    {
        Sidebar = sidebar;
        _navigation = navigation;
        _navigation.PropertyChanged += OnNavigationChanged;
    }

    public SidebarViewModel Sidebar { get; }
    public IMainViewModel Main => _navigation.Main;
    public IDialogViewModel? Dialog => _navigation.Dialog;

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceNavigation.Main) or nameof(WorkspaceNavigation.Dialog))
            OnPropertyChanged(e.PropertyName);
    }

    public void Dispose() => _navigation.PropertyChanged -= OnNavigationChanged;
}

public partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceNavigation _navigation;

    public SidebarViewModel(WorkspaceNavigation navigation)
    {
        _navigation = navigation;
        _navigation.PropertyChanged += OnNavigationChanged;
    }

    public string Selected => _navigation.Main is DocumentViewModel ? "Notes" : "Home";

    [RelayCommand]
    private void OpenHome() => _navigation.OpenHome();

    [RelayCommand]
    private void OpenNotes() => _navigation.OpenDocument();

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceNavigation.Main)) OnPropertyChanged(nameof(Selected));
    }

    public void Dispose() => _navigation.PropertyChanged -= OnNavigationChanged;
}

/// <summary>A saved note listed on the home page.</summary>
public sealed record SavedNote(string Title, string Excerpt);

/// <summary>Why a note could not be saved.</summary>
[RunicUnion(typeof(TitleRequired), typeof(TitleTaken))]
public abstract record SaveFailure;
/// <summary>The note has no title.</summary>
[RunicUnionCase("titleRequired")]
public sealed record TitleRequired : SaveFailure;
/// <summary>Another saved note already has the title.</summary>
[RunicUnionCase("titleTaken")]
public sealed record TitleTaken(string ExistingTitle) : SaveFailure;

/// <summary>The notes saved in one window, most recently saved first.</summary>
public sealed class NotesLibrary
{
    private readonly ObservableCollection<SavedNote> _notes = [];

    public NotesLibrary() => Notes = new(_notes);

    public ReadOnlyObservableCollection<SavedNote> Notes { get; }

    public bool Contains(string title) => _notes.Any(note => note.Title == title);

    // Saving a title again replaces its row and moves it to the top, so the
    // browser receives keyed collection changes rather than a new list.
    public void Record(string title, string body)
    {
        var note = new SavedNote(title, body.Length <= 40 ? body : body[..40] + "...");
        var index = _notes.Select(saved => saved.Title).ToList().IndexOf(title);
        if (index < 0)
        {
            _notes.Insert(0, note);
            return;
        }
        _notes[index] = note;
        if (index != 0) _notes.Move(index, 0);
    }
}

public partial class HomeViewModel(NotesLibrary library) : ObservableObject, IMainViewModel
{
    public string Greeting => "Welcome to composed Notes";

    /// <summary>The saved notes, most recent first.</summary>
    [RunicCollection(nameof(SavedNote.Title))]
    public ReadOnlyObservableCollection<SavedNote> RecentNotes => library.Notes;
}

public partial class DocumentViewModel : ObservableObject, IMainViewModel
{
    private readonly PreviewViewModel _preview;
    private IDocumentPaneViewModel _currentPane;

    public DocumentViewModel(EditorViewModel editor, PreviewViewModel preview)
    {
        Editor = editor;
        _preview = preview;
        _currentPane = editor;
    }

    internal EditorViewModel Editor { get; }

    public IDocumentPaneViewModel CurrentPane
    {
        get => _currentPane;
        private set
        {
            if (SetProperty(ref _currentPane, value)) OnPropertyChanged(nameof(ActivePane));
        }
    }

    /// <summary>The pane the document currently shows.</summary>
    public DocumentPane ActivePane => ReferenceEquals(CurrentPane, Editor) ? DocumentPane.Editor : DocumentPane.Preview;

    [RelayCommand]
    private void ShowEditor() => CurrentPane = Editor;

    [RelayCommand]
    private void ShowPreview() => CurrentPane = _preview;
}

public partial class EditorViewModel(INotesStorage storage, NotesLibrary library) : ObservableObject, IDocumentPaneViewModel
{
    [ObservableProperty] private string title = "Untitled";
    [ObservableProperty] private string body = "";
    private bool _isDirty;
    private string _savedMessage = "";
    private string _savedTitle = "Untitled";
    private string _savedBody = "";

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    public string SavedMessage
    {
        get => _savedMessage;
        private set => SetProperty(ref _savedMessage, value);
    }

    partial void OnTitleChanged(string value) => IsDirty = true;
    partial void OnBodyChanged(string value) => IsDirty = true;

    internal void DiscardChanges()
    {
        Title = _savedTitle;
        Body = _savedBody;
        IsDirty = false;
        SavedMessage = "Changes discarded.";
    }

    /// <summary>Saves the note. A missing title, or the title of another saved note, is a declared failure.</summary>
    [RelayCommand, RunicFailure(typeof(SaveFailure))]
    private async Task SaveAsync(CancellationToken token)
    {
        var title = Title;
        if (string.IsNullOrWhiteSpace(title))
            throw new RunicFailureException(new TitleRequired());
        // Saving under this editor's own saved title replaces that note; taking
        // the title of another note would overwrite it.
        if (title != _savedTitle && library.Contains(title))
            throw new RunicFailureException(new TitleTaken(title));
        var body = Body;
        await storage.SaveAsync(title, body, token);
        _savedTitle = title;
        _savedBody = body;
        IsDirty = Title != title || Body != body;
        SavedMessage = $"Saved {title}";
        library.Record(title, body);
    }
}

public partial class PreviewViewModel : ObservableObject, IDocumentPaneViewModel, IDisposable
{
    private readonly EditorViewModel _editor;

    public PreviewViewModel(EditorViewModel editor)
    {
        _editor = editor;
        _editor.PropertyChanged += OnEditorChanged;
    }

    public string Heading => _editor.Title;
    public string Excerpt => string.IsNullOrWhiteSpace(_editor.Body) ? "Nothing written yet." : _editor.Body;

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Title)) OnPropertyChanged(nameof(Heading));
        if (e.PropertyName == nameof(EditorViewModel.Body)) OnPropertyChanged(nameof(Excerpt));
    }

    public void Dispose() => _editor.PropertyChanged -= OnEditorChanged;
}

public partial class ConfirmNavigationViewModel(
    string message, Action confirm, Action cancel) : ObservableObject, IDialogViewModel
{
    public string Message => message;

    [RelayCommand]
    private void Confirm() => confirm();

    [RelayCommand]
    private void Cancel() => cancel();
}
