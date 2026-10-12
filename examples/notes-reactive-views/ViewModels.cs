using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Disposables;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

namespace NotesReactiveViews;

public interface IMainPage;
public interface IDocumentPane;

/// <summary>A pane of the document page.</summary>
public enum DocumentPane
{
    /// <summary>The note editor.</summary>
    Editor,

    /// <summary>The rendered note.</summary>
    Preview,
}
public interface IPinnedItem;

// The window's shell. Main is a RunicNavigator region: the generator presents its
// current page as a content slot. Home is borrowed from the window scope; each
// visit to the document pushes a fresh DocumentViewModel that the navigator owns.
// No command takes a scheduler: AddRunicReactiveModelContext delivers ReactiveUI's
// notifications on the window's model context.
public sealed class ShellViewModel : ReactiveObject, IDisposable
{
    private readonly HomeViewModel _home;
    private readonly PinnedNoteViewModel _pinnedNote = new();
    private readonly PinnedTaskViewModel _pinnedTask = new();
    private readonly MultipleDisposable _disposables = new();
    private IReadOnlyList<IPinnedItem> _pinned;

    public ShellViewModel(RunicNavigator navigator, HomeViewModel home, EditorViewModel editor, ILogger<ShellViewModel> logger)
    {
        _home = home;
        _pinned = [_pinnedNote, _pinnedTask];
        Editor = editor;
        Main = navigator.CreateRegion<IMainPage>(this, NavigationTarget.Borrow<IMainPage>(home));
        // Back runs the document's leave guard; it is enabled while Main has history.
        OpenHomeCommand = Main.CreateBackCommand().DisposeWith(_disposables);
        OpenDocumentCommand = ReactiveCommand.CreateFromTask(OpenDocumentAsync,
            Main.WhenCurrentChanged().Select(page => page is HomeViewModel)).DisposeWith(_disposables);
        OpenHomeCommand.ObserveBridgeExceptions(logger).DisposeWith(_disposables);
        OpenDocumentCommand.ObserveBridgeExceptions(logger).DisposeWith(_disposables);
        SwapPinnedCommand = ReactiveCommand.Create(SwapPinned).DisposeWith(_disposables);
        RemovePinnedCommand = ReactiveCommand.Create(RemovePinned).DisposeWith(_disposables);
        RestorePinnedCommand = ReactiveCommand.Create(RestorePinned).DisposeWith(_disposables);
    }

    public NavigationRegion<IMainPage> Main { get; }
    public IReadOnlyList<IPinnedItem> Pinned => _pinned;
    internal EditorViewModel Editor { get; }
    public ReactiveCommand<RxVoid, NavigationOutcome> OpenHomeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> OpenDocumentCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SwapPinnedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemovePinnedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RestorePinnedCommand { get; }

    // Master/detail: the document is pushed for a result, and Home shows how the
    // visit ended. The command ends when the push settles, so no browser call stays
    // open for the whole visit; ShowResultAsync waits for the document to close.
    // No token: a request's token stays live for the whole visit, and cancelling it
    // after the push commits would close the document.
    private async Task OpenDocumentAsync()
    {
        var request = Main.PushForResult<DocumentViewModel, string>();
        if ((await request.Transition).IsCommitted) _ = ShowResultAsync(request.Completion);
    }

    // Completion never faults: it ends Dismissed on every path but Save and close,
    // including a closing window. The continuation resumes on the model context.
    private async Task ShowResultAsync(Task<NavigationCompletion<string>> completion) =>
        _home.LastResult = await completion is NavigationCompletion<string>.Completed(var title)
            ? $"Saved and closed {title}"
            : "Closed the document.";

    private void SwapPinned()
    {
        _pinned = [_pinnedTask, _pinnedNote];
        this.RaisePropertyChanged(nameof(Pinned));
    }
    private void RemovePinned()
    {
        _pinned = [_pinnedTask];
        this.RaisePropertyChanged(nameof(Pinned));
    }
    private void RestorePinned()
    {
        _pinned = [_pinnedNote, _pinnedTask];
        this.RaisePropertyChanged(nameof(Pinned));
    }
    public void Dispose() => _disposables.Dispose();
}

public class PinnedNoteViewModel : ReactiveObject, IPinnedItem
{
    public string Label => "Pinned note";
}

public sealed class PinnedTaskViewModel : PinnedNoteViewModel
{
    public string Priority => "High";
}

public sealed partial class HomeViewModel : ReactiveObject, IMainPage
{
    private string _lastResult = "";
    public string Greeting => "Reactive Notes";
    /// <summary>How the last visit to the document ended.</summary>
    public string LastResult { get => _lastResult; internal set => this.RaiseAndSetIfChanged(ref _lastResult, value); }
}

// One visit to the document. The navigator constructs it with its entry context and
// owns it: it is disposed, and its child region closes, when the visit retires. The
// note lives in the window-scoped editor, which the document borrows, so the draft
// and the editor's View routes survive across visits.
public sealed class DocumentViewModel : ReactiveObject, IMainPage, INavigationDepartureGuard, IDisposable
{
    private readonly MultipleDisposable _disposables = new();
    private readonly EditorViewModel _editor;
    private readonly LeaveConfirmation _leave;
    private DocumentPane _activePane;
    private string _status = "";

    public DocumentViewModel(NavigationEntryContext entry, RunicNavigator navigator, EditorViewModel editor,
        PreviewViewModel preview, ILogger<DocumentViewModel> logger)
    {
        _editor = editor;
        CurrentPane = navigator.CreateRegion<IDocumentPane>(this, NavigationTarget.Borrow<IDocumentPane>(editor));
        CurrentPane.WhenCurrentChanged()
            .Subscribe(pane => ActivePane = pane is PreviewViewModel ? DocumentPane.Preview : DocumentPane.Editor)
            .DisposeWith(_disposables);
        ShowEditorCommand = ReactiveCommand.CreateFromTask(token => ShowAsync(editor, token)).DisposeWith(_disposables);
        ShowPreviewCommand = ReactiveCommand.CreateFromTask(token => ShowAsync(preview, token)).DisposeWith(_disposables);

        // Leaving with unsaved edits asks through ConfirmLeave. The edits are reverted
        // only when the departure commits; a No, or a superseded departure, keeps them.
        _leave = ReactiveLeaveConfirmation.WithInteraction(ConfirmLeave,
            () => new DiscardNoteRequest(editor.Title, editor.Body.Length), () => editor.IsDirty, editor.RevertChanges);
        // The web View answers for its own Close. The shell's Back has no handler there:
        // keep the edits rather than fail the departure (ReactiveUI asks the latest handler first).
        ConfirmLeave.RegisterHandler(context => context.SetOutput(false)).DisposeWith(_disposables);

        // Result commands end the entry: Save and close completes the request with the
        // title; Close goes back, asking first when there are unsaved edits.
        SaveAndCloseCommand = entry.CreateCompleteCommand(async token =>
        {
            await editor.SaveAsync(token);
            return editor.Title;
        }).DisposeWith(_disposables);
        CloseCommand = entry.CreateCloseCommand().DisposeWith(_disposables);

        // Per visit, not per mounted View: follow the draft while this entry is current.
        entry.WhenNavigatedTo((_, visit) => editor.WhenAnyValue(model => model.IsDirty)
            .Subscribe(dirty => Status = dirty ? "Unsaved changes" : "No unsaved changes")
            .DisposeWith(visit)).DisposeWith(_disposables);

        foreach (var command in new IHandleObservableErrors[] { ShowEditorCommand, ShowPreviewCommand, SaveAndCloseCommand, CloseCommand })
            command.ObserveBridgeExceptions(logger).DisposeWith(_disposables);
    }

    public NavigationRegion<IDocumentPane> CurrentPane { get; }
    [RunicViewContract("compact")]
    public EditorViewModel CompactNote => _editor;
    /// <summary>The pane the child region currently shows.</summary>
    public DocumentPane ActivePane { get => _activePane; private set => this.RaiseAndSetIfChanged(ref _activePane, value); }
    /// <summary>Whether the note has unsaved changes, while the document is current.</summary>
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }
    public Interaction<DiscardNoteRequest, bool> ConfirmLeave { get; } = new();
    public ReactiveCommand<RxVoid, RxVoid> ShowEditorCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowPreviewCommand { get; }
    /// <summary>Saves the note and closes the document with its title as the result.</summary>
    [RunicFailure(typeof(SaveFailure))]
    public ReactiveCommand<RxVoid, NavigationOutcome> SaveAndCloseCommand { get; }
    public ReactiveCommand<RxVoid, NavigationOutcome> CloseCommand { get; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        _leave.CanDepartAsync(departure, cancellationToken);

    // The panes are borrowed: replacing one retires its entry without disposing it.
    private async Task ShowAsync(IDocumentPane pane, CancellationToken token)
    {
        if (!ReferenceEquals(CurrentPane.Current, pane))
            await CurrentPane.ReplaceAsync(NavigationTarget.Borrow(pane), cancellationToken: token);
    }

    public void Dispose() => _disposables.Dispose();
}

public sealed record DiscardNoteRequest(string Title, int BodyLength);

/// <summary>Why a note could not be saved.</summary>
[RunicUnion(typeof(TitleRequired), typeof(TitleTooLong))]
public abstract record SaveFailure;
/// <summary>The note has no title.</summary>
[RunicUnionCase("titleRequired")]
public sealed record TitleRequired : SaveFailure;
/// <summary>The title is longer than the notes store accepts.</summary>
[RunicUnionCase("titleTooLong")]
public sealed record TitleTooLong(int MaximumLength) : SaveFailure;

public sealed class EditorViewModel : ReactiveObject, IDocumentPane, IActivatableViewModel, IDisposable
{
    private const int MaximumTitleLength = 120;
    private readonly IDisposable _fallbackDiscardHandler;
    private readonly IDisposable _saveExceptions;
    private readonly IDisposable _discardExceptions;
    private string _title = "Untitled";
    private string _body = "";
    private string _savedTitle = "Untitled";
    private string _savedBody = "";
    private string _savedMessage = "";
    private int _activationCount;
    private int _deactivationCount;

    public EditorViewModel(ILogger<EditorViewModel> logger)
    {
        _fallbackDiscardHandler = ConfirmDiscard.RegisterHandler(context =>
        {
            // A native or headless invocation has no mounted browser endpoint.
            // Keep ReactiveUI's normal handler precedence and decline the discard.
            context.SetOutput(false);
            return Task.CompletedTask;
        });
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        DiscardCommand = ReactiveCommand.CreateFromTask(DiscardAsync);
        // A ReactiveCommand also reports its exceptions on ThrownExceptions. The
        // Bridge already delivered declared failures; log anything else.
        _saveExceptions = SaveCommand.ObserveBridgeExceptions(logger);
        _discardExceptions = DiscardCommand.ObserveBridgeExceptions(logger);
        this.WhenActivated((Action<Action<IDisposable>>)(dispose =>
        {
            ActivationCount++;
            dispose(new ActivationLease(() => DeactivationCount++));
        }));
    }

    [RunicIgnore] public ViewModelActivator Activator { get; } = new();
    public string Title { get => _title; set { this.RaiseAndSetIfChanged(ref _title, value); this.RaisePropertyChanged(nameof(IsDirty)); } }
    public string Body { get => _body; set { this.RaiseAndSetIfChanged(ref _body, value); this.RaisePropertyChanged(nameof(IsDirty)); } }
    /// <summary>Whether the title or body differs from the last save.</summary>
    public bool IsDirty => _title != _savedTitle || _body != _savedBody;
    public string SavedMessage { get => _savedMessage; private set => this.RaiseAndSetIfChanged(ref _savedMessage, value); }
    public int ActivationCount { get => _activationCount; private set => this.RaiseAndSetIfChanged(ref _activationCount, value); }
    public int DeactivationCount { get => _deactivationCount; private set => this.RaiseAndSetIfChanged(ref _deactivationCount, value); }
    public Interaction<DiscardNoteRequest, bool> ConfirmDiscard { get; } = new();
    /// <summary>Saves the note. A missing or overlong title is a declared failure.</summary>
    [RunicFailure(typeof(SaveFailure))]
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }

    internal async Task SaveAsync(CancellationToken token)
    {
        // The bridge starts the body in a model-context turn, and every await
        // resumes in that context, so the body reads and sets state directly.
        var title = Title;
        var body = Body;
        if (string.IsNullOrWhiteSpace(title)) throw new RunicFailureException(new TitleRequired());
        if (title.Length > MaximumTitleLength) throw new RunicFailureException(new TitleTooLong(MaximumTitleLength));
        await Task.Delay(120, token);
        (_savedTitle, _savedBody) = (title, body);
        this.RaisePropertyChanged(nameof(IsDirty));
        SavedMessage = $"Saved {title}";
    }

    // The document's leave guard calls this in the commit turn of a confirmed departure.
    internal void RevertChanges()
    {
        Title = _savedTitle;
        Body = _savedBody;
        SavedMessage = "Reverted the unsaved changes.";
    }

    private async Task DiscardAsync(CancellationToken token)
    {
        var request = new DiscardNoteRequest(Title, Body.Length);
        if (request.BodyLength == 0)
        {
            SavedMessage = "Nothing to discard.";
            return;
        }

        bool approved = await ConfirmDiscard.Handle(request);
        token.ThrowIfCancellationRequested();
        if (approved)
        {
            Body = "";
            SavedMessage = $"Discarded {request.Title}";
        }
        else SavedMessage = "Kept current changes.";
    }

    public void Dispose()
    {
        _fallbackDiscardHandler.Dispose();
        _saveExceptions.Dispose();
        _discardExceptions.Dispose();
        SaveCommand.Dispose();
        DiscardCommand.Dispose();
    }

    private sealed class ActivationLease(Action dispose) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (!_disposed) { _disposed = true; dispose(); } }
    }
}

public sealed class PreviewViewModel : ReactiveObject, IDocumentPane, IDisposable
{
    private readonly EditorViewModel _editor;
    public PreviewViewModel(EditorViewModel editor)
    {
        _editor = editor;
        _editor.PropertyChanged += OnEditorChanged;
    }
    public string Heading => _editor.Title;
    public string Body => _editor.Body;
    private void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(EditorViewModel.Title)) this.RaisePropertyChanged(nameof(Heading));
        if (args.PropertyName == nameof(EditorViewModel.Body)) this.RaisePropertyChanged(nameof(Body));
    }
    public void Dispose() => _editor.PropertyChanged -= OnEditorChanged;
}
