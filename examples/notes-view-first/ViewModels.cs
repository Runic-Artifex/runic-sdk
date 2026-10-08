using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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

    // ExpectedCurrent makes a repeated click a no-op instead of a second document.
    public Task OpenNotesAsync() => !CanNavigate || Main.Current is DocumentViewModel
        ? Task.CompletedTask
        : Main.PushAsync(NavigationTarget.Create<IMainViewModel>(window => new DocumentViewModel(
                window.GetRequiredService<EditorViewModel>(), window.GetRequiredService<PreviewViewModel>(),
                window.GetRequiredService<RunicNavigator>(), window.GetRequiredService<IRunicModelContext>(), this)),
            new NavigationRequestOptions(Main.CurrentEntry?.Id)).AsTask();

    // The document's departure guard asks before unsaved edits are discarded. A guard's yes
    // is not a commit, so the document forgets it when the Back ends another way: superseded,
    // rejected or failed. Otherwise a later departure without guards, such as the window
    // closing, would discard the draft.
    public async Task OpenHomeAsync()
    {
        if (!CanNavigate || !Main.CanGoBack) return;
        var leaving = Main.Current as DocumentViewModel;
        var committed = false;
        try { committed = await Main.BackAsync() is NavigationResult<IMainViewModel>.Committed; }
        finally { if (!committed) leaving?.ForgetConfirmedDeparture(); } // also when Back throws
    }
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
    private readonly IRunicModelContext _context;
    private readonly WorkspaceNavigation _navigation;
    // Set by a confirmed departure; the discard runs in the turn that commits it.
    private volatile bool _discardOnDeparture;

    public DocumentViewModel(EditorViewModel editor, PreviewViewModel preview, RunicNavigator navigator,
        IRunicModelContext context, WorkspaceNavigation navigation)
    {
        Editor = editor;
        _preview = preview;
        _context = context;
        _navigation = navigation;
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

    // Leaving with unsaved edits asks in the dialog region. The guard's token
    // dismisses the dialog when a later navigation supersedes this one or the
    // window closes; the dialog's own token dismisses it when its answer can't commit.
    async ValueTask<bool> INavigationDepartureGuard.CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken)
    {
        _discardOnDeparture = false;
        if (departure.Kind != NavigationDepartureKind.Retire) return true;
        // Guards run outside model turns; read the draft on one.
        if (!await _context.InvokeAsync(() => Editor.IsDirty)) return true;
        var dialog = new ConfirmNavigationViewModel("Discard the unsaved edits and return Home?");
        using var answer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, dialog.Dismissal);
        var confirm = _navigation.Dialog.PushForResult<bool>(NavigationTarget.Own<IDialogViewModel>(dialog),
            cancellationToken: answer.Token);
        if (await confirm.Completion is not NavigationCompletion<bool>.Completed { Value: true }) return false;
        // Discard only when the Back commits: a request that supersedes it keeps the
        // document, and the draft with it.
        _discardOnDeparture = true;
        return true;
    }

    // Called when the Back that this guard allowed ends without committing.
    internal void ForgetConfirmedDeparture() => _discardOnDeparture = false;

    private void OnPaneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationRegion<IDocumentPaneViewModel>.Current)) OnPropertyChanged(nameof(ActivePane));
    }

    // Raised inside model turns; a change of Main's Current is raised in its commit turn.
    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _navigation.Main) && e.PropertyName == nameof(NavigationRegion<IMainViewModel>.Current)
            && _discardOnDeparture && !ReferenceEquals(_navigation.Main.Current, this))
        {
            _discardOnDeparture = false;
            Editor.DiscardChanges();
        }
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

// An owned dialog entry pushed for a result: confirming or cancelling completes
// the request and leaves the dialog region empty.
public partial class ConfirmNavigationViewModel(string message) : ObservableObject, IDialogViewModel, INavigationInitialize, IDisposable
{
    private readonly CancellationTokenSource _dismissal = new();
    private NavigationEntryContext? _entry;

    public string Message => message;

    // The asking guard links this into its request, so Cancel can always dismiss it.
    internal CancellationToken Dismissal => _dismissal.Token;

    ValueTask INavigationInitialize.InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken)
    {
        _entry = entry;
        return ValueTask.CompletedTask;
    }

    [RelayCommand]
    private Task Confirm() => AnswerAsync(true);

    [RelayCommand]
    private Task Cancel() => AnswerAsync(false);

    // An answer's Back can be rejected: superseded by a concurrent answer, not current, or its
    // commit turn failed. A rejected Confirm leaves the dialog open to answer again. Cancel
    // must always work, so it then dismisses the request: the guard keeps the document, and
    // the navigator goes back from the dialog.
    //
    // Overlapping Confirm and Cancel keep the document unless the Confirm's Back commits first.
    // The later answer's Back supersedes the earlier one, so a Cancel after a Confirm wins. A
    // Confirm after a Cancel supersedes the Cancel's Back, and then the Cancel dismisses the
    // request, whose Back supersedes the Confirm, unless the Confirm's Back commits before the
    // dismissal. A Confirm whose Back has started committing is final; a Cancel after that is
    // rejected, and the dialog is gone.
    private async Task AnswerAsync(bool confirmed)
    {
        if (_entry is not { } entry) return;
        if (await entry.CompleteAsync(confirmed) is NavigationResult<object>.Committed || confirmed) return;
        try { await _dismissal.CancelAsync(); }
        catch (ObjectDisposedException) { } // The dialog retired, so its request already ended.
    }

    public void Dispose() => _dismissal.Dispose();
}
