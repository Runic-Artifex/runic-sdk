using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;
using Runic.Navigation;

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

// The window's navigation: the main page and an in-page dialog, two regions of
// the window-scoped navigator. Home is borrowed from the window scope; each
// visit to the notes pushes a fresh owned document, retired when it is left.
public sealed class WorkspaceNavigation
{
    public WorkspaceNavigation(RunicNavigator navigator, HomeViewModel home)
    {
        Main = navigator.CreateRegion<IMainViewModel>(this, NavigationTarget.Borrow<IMainViewModel>(home));
        Dialog = navigator.CreateRegion<IDialogViewModel>(this);
    }

    public NavigationRegion<IMainViewModel> Main { get; }
    public NavigationRegion<IDialogViewModel> Dialog { get; }

    // The confirm dialog is modal: while it asks, or while a page navigation is in flight,
    // nothing else navigates. Commands bind their CanExecute to this, and the methods check it
    // again, so a caller that skips CanExecute can't supersede the Back that asks.
    public bool CanNavigate => Dialog.Current is null && !Main.IsTransitioning;

    // ExpectedCurrent makes a repeated click a no-op instead of a second document. The navigator
    // builds the document from the window's services and owns it.
    public Task OpenNotesAsync() => !CanNavigate || Main.Current is DocumentViewModel
        ? Task.CompletedTask
        : Main.PushAsync<DocumentViewModel>(new NavigationRequestOptions(Main.CurrentEntry?.Id)).AsTask();

    // The document's departure guard asks before unsaved edits are discarded.
    public Task OpenHomeAsync() => !CanNavigate || !Main.CanGoBack ? Task.CompletedTask : Main.BackAsync().AsTask();
}

// The generator presents each region's Current as a content slot; nothing is forwarded.
public partial class ShellViewModel(SidebarViewModel sidebar, WorkspaceNavigation navigation) : ObservableObject
{
    public SidebarViewModel Sidebar { get; } = sidebar;
    public NavigationRegion<IMainViewModel> Main => navigation.Main;
    public NavigationRegion<IDialogViewModel> Dialog => navigation.Dialog;
}

public partial class SidebarViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceNavigation _navigation;

    public SidebarViewModel(WorkspaceNavigation navigation)
    {
        _navigation = navigation;
        _navigation.Main.PropertyChanged += OnRegionChanged;
        _navigation.Dialog.PropertyChanged += OnRegionChanged;
    }

    public string Selected => _navigation.Main.Current is DocumentViewModel ? "Notes" : "Home";

    // Unavailable while the confirm dialog asks or a page navigation is in flight; the Bridge
    // rejects an unavailable command. A command completes when its navigation ends.
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task OpenHome() => _navigation.OpenHomeAsync();

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task OpenNotes() => _navigation.OpenNotesAsync();

    private bool CanNavigate() => _navigation.CanNavigate;

    // Regions raise their changes inside model turns.
    private void OnRegionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _navigation.Main) && e.PropertyName == nameof(NavigationRegion<IMainViewModel>.Current))
            OnPropertyChanged(nameof(Selected));
        OpenHomeCommand.NotifyCanExecuteChanged();
        OpenNotesCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _navigation.Main.PropertyChanged -= OnRegionChanged;
        _navigation.Dialog.PropertyChanged -= OnRegionChanged;
    }
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

// One visit to the notes. The navigator creates and owns it, and disposes it
// when it retires. The draft lives in the window-scoped editor, which the
// document borrows, so it survives across visits.
public partial class DocumentViewModel : ObservableObject, IMainViewModel, INavigationInitialize, INavigationDepartureGuard, IDisposable
{
    private readonly PreviewViewModel _preview;
    private readonly WorkspaceNavigation _navigation;
    private readonly LeaveConfirmation _leave;

    public DocumentViewModel(EditorViewModel editor, PreviewViewModel preview, RunicNavigator navigator, WorkspaceNavigation navigation)
    {
        Editor = editor;
        _preview = preview;
        _navigation = navigation;
        // Leaving with unsaved edits asks in the dialog region, and discards the edits only when
        // the departure commits. A superseded departure or a closing window dismisses the dialog.
        _leave = LeaveConfirmation.InDialog(navigation.Dialog,
            () => NavigationTarget.Own<IDialogViewModel>(new ConfirmNavigationViewModel("Discard the unsaved edits and return Home?")),
            () => Editor.IsDirty, Editor.DiscardChanges);
        // A child region owned by this document: it closes when the document retires,
        // and keeps the open pane while another page covers the document.
        CurrentPane = navigator.CreateRegion<IDocumentPaneViewModel>(this, NavigationTarget.Borrow<IDocumentPaneViewModel>(editor),
            new NavigationRegionOptions(NavigationChildRetention.Keep));
        CurrentPane.PropertyChanged += OnPaneChanged;
        _navigation.Main.PropertyChanged += OnWorkspaceChanged;
        _navigation.Dialog.PropertyChanged += OnWorkspaceChanged;
    }

    internal EditorViewModel Editor { get; }

    // The navigator initializes an entry once; returning to it resumes it instead.
    internal int Initializations { get; private set; }

    public NavigationRegion<IDocumentPaneViewModel> CurrentPane { get; }

    /// <summary>The pane the document currently shows.</summary>
    public DocumentPane ActivePane => ReferenceEquals(CurrentPane.Current, _preview) ? DocumentPane.Preview : DocumentPane.Editor;

    // Modal like the sidebar: a pane change committed while the guard asks would
    // supersede the Back that asks and silently dismiss the dialog.
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task ShowEditor() => CanNavigate() && CurrentPane.CanGoBack ? CurrentPane.BackAsync().AsTask() : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task ShowPreview() => !CanNavigate() || CurrentPane.Current is PreviewViewModel
        ? Task.CompletedTask
        : CurrentPane.PushAsync(NavigationTarget.Borrow<IDocumentPaneViewModel>(_preview),
            new NavigationRequestOptions(CurrentPane.CurrentEntry?.Id)).AsTask();

    private bool CanNavigate() => _navigation.CanNavigate;

    ValueTask INavigationInitialize.InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        Initializations++;
        return ValueTask.CompletedTask;
    }

    ValueTask<bool> INavigationDepartureGuard.CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        _leave.CanDepartAsync(departure, cancellationToken);

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationRegion<IDocumentPaneViewModel>.Current)) OnPropertyChanged(nameof(ActivePane));
    }

    // Raised inside model turns.
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        ShowEditorCommand.NotifyCanExecuteChanged();
        ShowPreviewCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        CurrentPane.PropertyChanged -= OnPaneChanged;
        _navigation.Main.PropertyChanged -= OnWorkspaceChanged;
        _navigation.Dialog.PropertyChanged -= OnWorkspaceChanged;
    }
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

// An owned dialog entry pushed for a result: confirming completes the request,
// cancelling dismisses it, and either leaves the dialog region empty.
public partial class ConfirmNavigationViewModel(string message) : ObservableObject, IDialogViewModel, INavigationInitialize
{
    private NavigationEntryContext? _entry;

    public string Message => message;

    ValueTask INavigationInitialize.InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        _entry = entry;
        return ValueTask.CompletedTask;
    }

    // A Confirm whose Back is rejected (superseded by a concurrent answer, or its commit turn
    // failed) leaves the dialog open to answer again. Cancel always works: it ends the request
    // at once, so the guard keeps the document, and then goes back from the dialog. A Cancel
    // after a Confirm supersedes the Confirm's Back unless that Back has started committing.
    [RelayCommand]
    private Task Confirm() => _entry is { } entry ? entry.CompleteAsync(true).AsTask() : Task.CompletedTask;

    [RelayCommand]
    private Task Cancel() => _entry is { } entry ? entry.DismissAsync().AsTask() : Task.CompletedTask;
}
